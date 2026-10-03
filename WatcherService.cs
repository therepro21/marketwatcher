namespace MarketWatcher;

public sealed class WatcherService : IAsyncDisposable
{
    private readonly Repository _repo; private readonly Action<string> _log; private readonly Action _refresh;
    private readonly BrowserScanner _scanner = new(); private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1,1); private Task? _loop;
    public WatcherService(Repository repo, Action<string> log, Action refresh){_repo=repo;_log=log;_refresh=refresh;}
    public Task StartAsync(){_loop=LoopAsync();return Task.CompletedTask;}
    private async Task LoopAsync()
    {
        while(!_stop.IsCancellationRequested)
        {
            var jobs=_repo.GetJobs().Where(x=>x.Enabled).ToList();
            foreach(var j in jobs.Where(x=>x.LastRunUtc is null || DateTime.UtcNow-x.LastRunUtc.Value>=TimeSpan.FromSeconds(x.IntervalSeconds)))
                await RunJobAsync(j.Id,false);
            // Sleep until work can actually become due (capped so UI changes are
            // noticed promptly) instead of waking the process every second.
            var enabled=_repo.GetJobs().Where(x=>x.Enabled).ToList();
            var wait=enabled.Count==0?TimeSpan.FromSeconds(30):enabled
                .Select(x=>x.LastRunUtc is null?TimeSpan.Zero:TimeSpan.FromSeconds(x.IntervalSeconds)-(DateTime.UtcNow-x.LastRunUtc.Value))
                .Select(x=>x<TimeSpan.Zero?TimeSpan.Zero:x).DefaultIfEmpty(TimeSpan.FromSeconds(30)).Min();
            if(wait<TimeSpan.FromMilliseconds(250))wait=TimeSpan.FromMilliseconds(250);
            if(wait>TimeSpan.FromSeconds(30))wait=TimeSpan.FromSeconds(30);
            try{await Task.Delay(wait,_stop.Token);}catch(OperationCanceledException){break;}
        }
    }
    public async Task RunJobAsync(long id,bool added)
    {
        if(!await _gate.WaitAsync(0)){_log("Ein Suchlauf ist bereits aktiv.");return;}
        try
        {
            var job=_repo.GetJob(id); if(job is null)return; _log($"Prüfe {job.Name} …");
            var items=await _scanner.ScanAsync(job); var initial=!job.Initialized; var fresh=new List<Listing>();
            foreach(var item in items) if(_repo.AddSeen(job.Id,BrowserScanner.Fingerprint(job.Platform,item),item,!initial)) fresh.Add(item);
            if(initial){_repo.UpdateRun(job.Id,true,$"Basisbestand: {items.Count} Treffer");_log($"{job.Name}: {items.Count} bestehende Treffer ausgeschlossen.");}
            else if(fresh.Count==0){_repo.UpdateRun(job.Id,true,$"Keine Änderungen · {items.Count} Treffer");_log($"{job.Name}: nichts Neues.");}
            else
            {
                var errors=await SendNotificationsAsync(job,fresh);
                _repo.UpdateRun(job.Id,true,errors.Count==0?$"{fresh.Count} neue Treffer gemeldet":$"{fresh.Count} neu · {errors.Count} Meldefehler");
                _log($"{job.Name}: {fresh.Count} neue Treffer."+(errors.Count==0?"":$" Fehler: {string.Join(" | ",errors)}"));
            }
        }
        catch(BrowserChallengeException ex){_repo.UpdateRun(id,true,"Pausiert: Browser-Prüfung");_repo.SetEnabled(id,false);_log(ex.Message);}
        catch(Exception ex){_repo.UpdateRun(id,_repo.GetJob(id)?.Initialized??false,"Fehler: "+ex.Message);_log("Fehler: "+ex.Message);}
        finally
        {
            try{await _scanner.CloseAsync();}catch(Exception ex){_log("Browser konnte nicht vollständig geschlossen werden: "+ex.Message);}
            _gate.Release();_refresh();
        }
    }
    public async Task OpenProfileAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await _scanner.CloseAsync();
            var edge=BrowserFinder.FindEdge()??throw new InvalidOperationException("Microsoft Edge wurde nicht gefunden.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(edge,$"--user-data-dir=\"{AppPaths.EdgeProfile}\""){UseShellExecute=true});
            _log("Edge-Profil geöffnet. Nach Anmeldung/Prüfung Edge schließen und die Suche wieder aktivieren.");
        }
        finally
        {
            try{await _scanner.CloseAsync();}catch(Exception ex){_log("Browser konnte nicht vollständig geschlossen werden: "+ex.Message);}
            _gate.Release();
        }
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
            var job=new SearchJob{Name="Testsuche",Platform="MarktWächter"};
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
        if(_loop is not null)try{await _loop;}catch(OperationCanceledException){}catch(Exception ex){_log("Scheduler beim Beenden: "+ex.Message);}
        await _gate.WaitAsync();
        try{await _scanner.DisposeAsync();}finally{_gate.Release();}
        _stop.Dispose();_gate.Dispose();
    }
}
