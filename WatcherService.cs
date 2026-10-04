namespace MarketWatcher;

public sealed class WatcherService : IAsyncDisposable
{
    public event Action<BrowserChallengeException>? ManualInterventionRequired;
    private readonly Repository _repo; private readonly Action<string> _log; private readonly Action _refresh;
    private readonly BrowserScanner _scanner = new(); private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1,1);private readonly SemaphoreSlim _wake = new(0,1); private Task? _loop;
    private System.Diagnostics.Process? _manualEdgeProcess;
    public WatcherService(Repository repo, Action<string> log, Action refresh){_repo=repo;_log=log;_refresh=refresh;}
    public Task StartAsync(){_loop=LoopAsync();return Task.CompletedTask;}
    private async Task LoopAsync()
    {
        while(!_stop.IsCancellationRequested)
        {
            var jobs=_repo.GetJobs().Where(x=>x.Enabled).ToList();
            foreach(var j in jobs.Where(x=>x.LastRunUtc is null || DateTime.UtcNow-x.LastRunUtc.Value>=TimeSpan.FromSeconds(x.IntervalSeconds)))
                await RunJobAsync(j.Id,false);
            // Sleep until work is actually due. UI changes signal _wake, so an
            // idle installation consumes no CPU through periodic polling.
            var enabled=_repo.GetJobs().Where(x=>x.Enabled).ToList();
            if(enabled.Count==0)
            {
                if(_scanner.IsRunning)try{await _scanner.CloseAsync();_log("Keine aktive Suche · Edge vollständig geschlossen.");}catch(Exception ex){_log("Browser konnte nicht vollständig geschlossen werden: "+ex.Message);}
                try{await _wake.WaitAsync(_stop.Token);}catch(OperationCanceledException){break;}
                continue;
            }
            var wait=enabled
                .Select(x=>x.LastRunUtc is null?TimeSpan.Zero:TimeSpan.FromSeconds(x.IntervalSeconds)-(DateTime.UtcNow-x.LastRunUtc.Value))
                .Select(x=>x<TimeSpan.Zero?TimeSpan.Zero:x).Min();
            if(wait<TimeSpan.FromMilliseconds(250))wait=TimeSpan.FromMilliseconds(250);
            try{await _wake.WaitAsync(wait,_stop.Token);}catch(OperationCanceledException){break;}
        }
    }
    public void NotifyScheduleChanged(){if(_wake.CurrentCount==0)_wake.Release();}
    public async Task RunJobAsync(long id,bool added)
    {
        if(!await _gate.WaitAsync(0)){_log("Ein Suchlauf ist bereits aktiv.");return;}
        var elapsed=System.Diagnostics.Stopwatch.StartNew();var reusedBrowser=_scanner.IsRunning;
        try
        {
            var job=_repo.GetJob(id); if(job is null)return; _log($"SUCHE #{job.Id} · PORTAL: {job.Platform} · SUCHBEGRIFF: {job.Name} · Prüfung läuft …");
            var items=await _scanner.ScanAsync(job); var initial=!job.Initialized; var fresh=new List<Listing>();
            if(_repo.GetJob(id)?.Enabled!=true){_log($"{job.Name}: Prüfung abgebrochen, weil die Suche pausiert wurde.");return;}
            foreach(var item in items) if(_repo.AddSeen(job.Id,BrowserScanner.Fingerprint(job.Platform,item),item,!initial)) fresh.Add(item);
            if(initial){var now=DateTime.UtcNow;_repo.SetLastNewResult(job.Id,now);var status=$"PORTAL: {job.Platform} · SUCHBEGRIFF: {job.Name} · Basisbestand: {items.Count} Treffer";_repo.UpdateRun(job.Id,true,status);_log($"SUCHE #{job.Id} · {status}");}
            else if(fresh.Count==0){var since=FormatElapsed(DateTime.UtcNow-(job.LastNewResultUtc??job.LastRunUtc??DateTime.UtcNow));var status=$"PORTAL: {job.Platform} · SUCHBEGRIFF: {job.Name} · Seit {since} keine neuen Ergebnisse · {items.Count} aktuelle Treffer";_repo.UpdateRun(job.Id,true,status);_log($"SUCHE #{job.Id} · {status}");}
            else
            {
                var errors=await SendNotificationsAsync(job,fresh);
                _repo.SetLastNewResult(job.Id,DateTime.UtcNow);var status=$"PORTAL: {job.Platform} · SUCHBEGRIFF: {job.Name} · {fresh.Count} neue Ergebnisse"+(errors.Count==0?" und gemeldet":$" · {errors.Count} Meldefehler");_repo.UpdateRun(job.Id,true,status);
                _log($"SUCHE #{job.Id} · {status}"+(errors.Count==0?"":$" · {string.Join(" | ",errors)}"));
            }
        }
        catch(BrowserChallengeException ex){var failed=_repo.GetJob(id);_repo.UpdateRun(id,true,$"PORTAL: {failed?.Platform??ex.Platform} · SUCHBEGRIFF: {failed?.Name??ex.SearchName} · Pausiert: {ex.Message}");_repo.SetEnabled(id,false);_log($"SUCHE #{id} · PORTAL: {ex.Platform} · SUCHBEGRIFF: {ex.SearchName} · {ex.Message}");ManualInterventionRequired?.Invoke(ex);}
        catch(Exception ex){var failed=_repo.GetJob(id);_repo.UpdateRun(id,failed?.Initialized??false,$"PORTAL: {failed?.Platform??"Unbekannt"} · SUCHBEGRIFF: {failed?.Name??"Unbekannt"} · Fehler: {ex.Message}");_log($"SUCHE #{id} · PORTAL: {failed?.Platform??"Unbekannt"} · SUCHBEGRIFF: {failed?.Name??"Unbekannt"} · FEHLER: {ex.Message}");}
        finally
        {
            var keepBrowser=_repo.GetGeneral().KeepBrowserOpen&&_repo.GetJobs().Any(x=>x.Enabled);
            if(!keepBrowser)try{await _scanner.CloseAsync();}catch(Exception ex){_log("Browser konnte nicht vollständig geschlossen werden: "+ex.Message);}
            elapsed.Stop();_log($"SUCHE #{id} · Laufzeit {elapsed.Elapsed.TotalSeconds:0.0} Sek. · Edge {(reusedBrowser?keepBrowser?"wiederverwendet und bleibt geöffnet":"wiederverwendet und geschlossen":keepBrowser?"gestartet und bleibt unsichtbar geöffnet":"neu gestartet und geschlossen")}");
            _gate.Release();_refresh();
        }
    }
    private static string FormatElapsed(TimeSpan elapsed)
    {
        if(elapsed<TimeSpan.Zero)elapsed=TimeSpan.Zero;
        if(elapsed.TotalMinutes<1)return "weniger als 1 Min.";
        if(elapsed.TotalDays>=1)return $"{(int)elapsed.TotalDays} Tg. {elapsed.Hours} Std. {elapsed.Minutes} Min.";
        if(elapsed.TotalHours>=1)return $"{(int)elapsed.TotalHours} Std. {elapsed.Minutes} Min.";
        return $"{elapsed.Minutes} Min.";
    }
    public async Task ResumeFromNowAsync(long id)
    {
        if(!await _gate.WaitAsync(0)){_log("Ein Suchlauf ist bereits aktiv.");return;}
        try
        {
            var job=_repo.GetJob(id);if(job is null)return;
            _log($"{job.Name}: aktueller Stand wird ohne Meldungen übernommen …");
            var items=await _scanner.ScanAsync(job);
            foreach(var item in items)_repo.AddSeen(job.Id,BrowserScanner.Fingerprint(job.Platform,item),item,false);
            _repo.UpdateRun(job.Id,true,$"Fortgesetzt ab jetzt · {items.Count} aktuelle Treffer ausgeschlossen");
            _repo.SetEnabled(job.Id,true);NotifyScheduleChanged();_log($"{job.Name}: fortgesetzt; Meldungen gelten ab jetzt.");
        }
        catch(Exception ex){_repo.SetEnabled(id,false);_log("Fortsetzen fehlgeschlagen: "+ex.Message);}
        finally{if(!_repo.GetGeneral().KeepBrowserOpen)try{await _scanner.CloseAsync();}catch{} _gate.Release();_refresh();}
    }
    public async Task ApplyBrowserPolicyAsync()
    {
        if(_repo.GetGeneral().KeepBrowserOpen)return;
        if(!await _gate.WaitAsync(0)){_log("Browsermodus geändert: Edge wird nach dem laufenden Suchlauf beendet.");return;}
        try{await _scanner.CloseAsync();_log("Browsermodus geändert: Edge wird zwischen Prüfungen vollständig beendet.");}
        finally{_gate.Release();}
    }
    public async Task OpenProfileAsync(string? url=null)
    {
        await _gate.WaitAsync();
        try
        {
            await _scanner.CloseAsync();
            var edge=BrowserFinder.FindEdge()??throw new InvalidOperationException("Microsoft Edge wurde nicht gefunden.");
            var start=new System.Diagnostics.ProcessStartInfo(edge){UseShellExecute=true};
            start.UseShellExecute=false;start.ArgumentList.Add($"--user-data-dir={System.IO.Path.GetFullPath(AppPaths.EdgeProfile)}");start.ArgumentList.Add("--new-window");start.ArgumentList.Add("--window-position=100,100");start.ArgumentList.Add("--window-size=1400,900");start.ArgumentList.Add("--start-maximized");
            if(!string.IsNullOrWhiteSpace(url))start.ArgumentList.Add(url);
            _manualEdgeProcess=System.Diagnostics.Process.Start(start);
            _log("Edge-Profil geöffnet. Nach Anmeldung/Prüfung Edge schließen und die Suche wieder aktivieren.");
        }
        finally
        {
            try{await _scanner.CloseAsync();}catch(Exception ex){_log("Browser konnte nicht vollständig geschlossen werden: "+ex.Message);}
            _gate.Release();
        }
    }
    public async Task CompleteManualInterventionAsync()
    {
        await _gate.WaitAsync();
        try{await CompleteManualInterventionCoreAsync();}
        finally{_gate.Release();}
    }
    private async Task CompleteManualInterventionCoreAsync()
    {
        var process=_manualEdgeProcess;_manualEdgeProcess=null;
        if(process is not null)try{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));}}catch{}finally{process.Dispose();}
        await CleanupProfileEdgeProcessesAsync();
        BrowserScanner.CleanDisposableCaches();
        _log("Sichtbarer Prüf-Browser geschlossen. Weitere Läufe starten wieder unsichtbar.");
    }
    private static async Task CleanupProfileEdgeProcessesAsync()
    {
        var profile=System.IO.Path.GetFullPath(AppPaths.EdgeProfile).Replace("'","''");
        var script="$profile='"+profile+"'; Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($profile) } | Select-Object -ExpandProperty ProcessId";
        var start=new System.Diagnostics.ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-WindowStyle");start.ArgumentList.Add("Hidden");start.ArgumentList.Add("-Command");start.ArgumentList.Add(script);
        try
        {
            using var query=System.Diagnostics.Process.Start(start);if(query is null)return;var output=await query.StandardOutput.ReadToEndAsync();await query.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            foreach(var line in output.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))if(int.TryParse(line.Trim(),out var pid))try{using var edge=System.Diagnostics.Process.GetProcessById(pid);edge.Kill(true);await edge.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch{}
        }
        catch{}
    }
    private async Task<List<string>> SendNotificationsAsync(SearchJob job,IReadOnlyList<Listing> items)
    {
        var errors=new List<string>();
        try{await EmailSender.SendAsync(_repo.GetEmail(),job,items);}catch(Exception ex){errors.Add("E-Mail: "+ex.Message);}
        try{var tg=_repo.GetTelegram();var selected=job.TelegramRecipientIds.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);foreach(var recipient in tg.Recipients)recipient.Selected=selected.Contains(recipient.Id);await TelegramSender.SendAsync(tg,job,items);}catch(Exception ex){errors.Add("Telegram: "+ex.Message);}
        try
        {
            var wa=_repo.GetWhatsApp();var selected=job.RecipientIds.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var recipient in wa.Recipients)recipient.Selected=selected.Contains(recipient.Id);
            await SendWhatsAppConfiguredAsync(wa,job,items);
        }catch(Exception ex){errors.Add("WhatsApp: "+ex.Message);}
        return errors;
    }
    public async Task TestWhatsAppAsync(WhatsAppSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            var job=new SearchJob{Name="Testsuche",Platform="MarketWatcher"};
            await SendWhatsAppConfiguredAsync(settings,job,[new Listing("test","Automatische Testnachricht erfolgreich","https://example.com","")]);
        }
        finally{_gate.Release();}
    }
    private async Task SendWhatsAppConfiguredAsync(WhatsAppSettings settings,SearchJob job,IReadOnlyList<Listing> items)
    {
        if(!settings.Enabled)return;
        if(!settings.UseDesktopApp){await _scanner.SendWhatsAppAsync(settings,job,items);return;}
        var recipients=new List<(string Name,string Phone)>();
        if(settings.SendToFirst)recipients.Add((settings.RecipientName,settings.RecipientNumber));
        if(settings.SendToSecond)recipients.Add((settings.Recipient2Name,settings.Recipient2Number));
        if(recipients.Count==0)throw new InvalidOperationException("Kein WhatsApp-Empfänger ausgewählt.");
        foreach(var recipient in recipients)
        foreach(var message in NotificationText.BuildChunks(job,items,3000))
            await WhatsAppDesktopSender.SendAsync(recipient.Name,recipient.Phone,message);
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        NotifyScheduleChanged();
        if(_loop is not null)try{await _loop;}catch(OperationCanceledException){}catch(Exception ex){_log("Scheduler beim Beenden: "+ex.Message);}
        await _gate.WaitAsync();
        try{await _scanner.DisposeAsync();await CompleteManualInterventionCoreAsync();await CleanupProfileEdgeProcessesAsync();}finally{_gate.Release();}
        _stop.Dispose();_gate.Dispose();_wake.Dispose();
    }
}
