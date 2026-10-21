namespace MarketWatcher;

public sealed class WatcherService : IAsyncDisposable
{
    public event Action<BrowserChallengeException>? ManualInterventionRequired;
    private readonly Repository _repo; private readonly Action<string> _log; private readonly Action _refresh;
    private readonly BrowserScanner _scanner = new(); private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1,1);private readonly SemaphoreSlim _wake = new(0,1); private Task? _loop;
    private System.Diagnostics.Process? _manualEdgeProcess;
    public WatcherService(Repository repo, Action<string> log, Action refresh){_repo=repo;_log=log;_refresh=refresh;_scanner.Diagnostic+=log;}
    public bool IsGloballyPaused=>_repo.GetGeneral().GloballyPaused;
    public Task StartAsync(){_scanner.Suspended=IsGloballyPaused;_loop=LoopAsync();return Task.CompletedTask;}
    public async Task PauseAllAsync()
    {
        _repo.SetGlobalPause(true);NotifyScheduleChanged();_refresh();
        await _scanner.SuspendAsync();
        await _gate.WaitAsync();
        try{await _scanner.CloseAsync();await CompleteManualInterventionCoreAsync();_log("MarketWatcher global pausiert · Edge geschlossen · Suchauswahl bleibt erhalten.");}
        finally{_gate.Release();_refresh();}
    }
    public void ResumeAll(bool includeMissed)
    {
        _repo.SetGlobalPause(false,includeMissed);_scanner.Suspended=false;
        _log(includeMissed?"MarketWatcher fortgesetzt · verpasste Treffer werden geprüft.":"MarketWatcher fortgesetzt · aktueller Bestand wird je aktiver Suche still übernommen.");
        NotifyScheduleChanged();_refresh();
    }
    private async Task LoopAsync()
    {
        while(!_stop.IsCancellationRequested)
        {
            if(IsGloballyPaused)
            {
                try{await _wake.WaitAsync(_stop.Token);}catch(OperationCanceledException){break;}
                continue;
            }
            var jobs=_repo.GetJobs().Where(x=>x.Enabled).ToList();
            foreach(var j in jobs.Where(x=>x.LastRunUtc is null || DateTime.UtcNow-x.LastRunUtc.Value>=TimeSpan.FromSeconds(x.IntervalSeconds)))
                await RunJobAsync(j.Id,false);
            if(IsGloballyPaused)continue;
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
            if(IsGloballyPaused)return;
            var job=_repo.GetJob(id); if(job is null)return; _log($"SUCHE #{job.Id} · PORTAL: {job.Platform} · SUCHBEGRIFF: {job.Name} · Prüfung läuft …");
            if(job.Platform=="Amazon"){await RunAmazonAsync(job);return;}
            var items=await _scanner.ScanAsync(job); var initial=!job.Initialized||job.SkipBacklogOnce; var fresh=new List<Listing>();
            if(IsGloballyPaused)return;
            if(_repo.GetJob(id)?.Enabled!=true){_log($"{job.Name}: Prüfung abgebrochen, weil die Suche pausiert wurde.");return;}
            foreach(var item in items) if(_repo.AddSeen(job.Id,BrowserScanner.Fingerprint(job.Platform,item),item,!initial)) fresh.Add(item);
            if(job.SkipBacklogOnce)_repo.CompleteBacklogBaseline(job.Id);
            if(initial){var now=DateTime.UtcNow;_repo.SetLastNewResult(job.Id,now);var status=$"PORTAL: {job.Platform} · SUCHBEGRIFF: {job.Name} · Basisbestand: {items.Count} Treffer";_repo.UpdateRun(job.Id,true,status);_log($"SUCHE #{job.Id} · {status}");}
            else if(fresh.Count==0&&_repo.GetPending(job.Id).Count==0){var since=FormatElapsed(DateTime.UtcNow-(job.LastNewResultUtc??job.LastRunUtc??DateTime.UtcNow));var status=$"PORTAL: {job.Platform} · SUCHBEGRIFF: {job.Name} · Seit {since} keine neuen Ergebnisse · {items.Count} aktuelle Treffer";_repo.UpdateRun(job.Id,true,status);_log($"SUCHE #{job.Id} · {status}");}
            else
            {
                var pendingBefore=_repo.GetPending(job.Id).Count;
                var errors=await SendQueuedAsync(job);
                var waiting=_repo.GetPending(job.Id).Count;
                _repo.RecordSuccessfulNotifications(pendingBefore-waiting);if(errors.Count>0)_repo.RecordErrors(errors.Count);
                if(fresh.Count>0)_repo.SetLastNewResult(job.Id,DateTime.UtcNow);var status=$"PORTAL: {job.Platform} · SUCHBEGRIFF: {job.Name} · {fresh.Count} neue Ergebnisse · {pendingBefore-waiting} Meldungen bestätigt · {waiting} warten auf Versand"+(errors.Count==0?"":$" · {errors.Count} Meldefehler");_repo.UpdateRun(job.Id,true,status);
                _log($"SUCHE #{job.Id} · {status}"+(errors.Count==0?"":$" · {string.Join(" | ",errors)}"));
            }
        }
        catch(Exception) when(IsGloballyPaused){_log($"SUCHE #{id} · durch globale Pause beendet.");}
        catch(BrowserChallengeException ex){_repo.RecordErrors();var failed=_repo.GetJob(id);_repo.UpdateRun(id,true,$"PORTAL: {failed?.Platform??ex.Platform} · SUCHBEGRIFF: {failed?.Name??ex.SearchName} · Pausiert: {ex.Message}");_repo.SetEnabled(id,false);_log($"SUCHE #{id} · PORTAL: {ex.Platform} · SUCHBEGRIFF: {ex.SearchName} · {ex.Message}");ManualInterventionRequired?.Invoke(ex);}
        catch(Exception ex){_repo.RecordErrors();var failed=_repo.GetJob(id);_repo.UpdateRun(id,failed?.Initialized??false,$"PORTAL: {failed?.Platform??"Unbekannt"} · SUCHBEGRIFF: {failed?.Name??"Unbekannt"} · Fehler: {ex.Message}");_log($"SUCHE #{id} · PORTAL: {failed?.Platform??"Unbekannt"} · SUCHBEGRIFF: {failed?.Name??"Unbekannt"} · FEHLER: {ex.Message}");}
        finally
        {
            var keepBrowser=!IsGloballyPaused&&_repo.GetGeneral().KeepBrowserOpen&&_repo.GetJobs().Any(x=>x.Enabled);
            if(!keepBrowser)try{await _scanner.CloseAsync();}catch(Exception ex){_log("Browser konnte nicht vollständig geschlossen werden: "+ex.Message);}
            elapsed.Stop();_log($"SUCHE #{id} · Laufzeit {elapsed.Elapsed.TotalSeconds:0.0} Sek. · Edge {(_scanner.IsRunning?"verbunden und bleibt unsichtbar geöffnet":keepBrowser?"nicht verbunden · automatischer neuer Versuch beim nächsten Lauf":"geschlossen")}");
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
    private async Task RunAmazonAsync(SearchJob job)
    {
        var observation=await _scanner.ScanAmazonAsync(job);
        if(IsGloballyPaused||_repo.GetJob(job.Id)?.Enabled!=true)return;
        if(job.Name.StartsWith("Amazon · ",StringComparison.Ordinal))_repo.UpdateAmazonTitle(job.Id,observation.Title);
        var evaluation=AmazonRules.Evaluate(job,observation);
        if(evaluation.Alerts.Count>0&&!job.SkipBacklogOnce)
        {
            var listing=new Listing(AmazonRules.Asin(job.Url),observation.Title,job.Url,string.Join(" · ",evaluation.Alerts),observation.ImageUrl);
            _repo.QueueAmazonNotification(job.Id,listing,evaluation.Conditions);
            _repo.SetLastNewResult(job.Id,DateTime.UtcNow);
            _log($"SUCHE #{job.Id} · Amazon · {observation.Title} · {string.Join(" · ",evaluation.Alerts)}");
        }
        else _repo.UpdateAmazonObservation(job.Id,evaluation.Conditions);
        var pendingBefore=_repo.GetPending(job.Id).Count;
        if(pendingBefore>0)
        {
            var errors=await SendQueuedAsync(job);
            _repo.RecordSuccessfulNotifications(pendingBefore-_repo.GetPending(job.Id).Count);
            if(errors.Count>0){_repo.RecordErrors(errors.Count);_log($"SUCHE #{job.Id} · Amazon · Versand zurückgehalten: "+string.Join(" | ",errors));}
        }
        if(job.SkipBacklogOnce)_repo.CompleteBacklogBaseline(job.Id);
        _repo.UpdateRun(job.Id,true,"Amazon · "+evaluation.Summary);
        _log($"SUCHE #{job.Id} · Amazon · {evaluation.Summary}");
    }
    public async Task ResumeFromNowAsync(long id)
    {
        if(IsGloballyPaused){_log("MarketWatcher ist global pausiert. Zuerst im Tray fortsetzen.");return;}
        if(!await _gate.WaitAsync(0)){_log("Ein Suchlauf ist bereits aktiv.");return;}
        try
        {
            var job=_repo.GetJob(id);if(job is null)return;
            _log($"{job.Name}: aktueller Stand wird ohne Meldungen übernommen …");
            if(job.Platform=="Amazon")
            {
                var observation=await _scanner.ScanAmazonAsync(job);
                var evaluation=AmazonRules.Evaluate(job,observation);
                _repo.UpdateAmazonObservation(job.Id,evaluation.Conditions);
                _repo.UpdateRun(job.Id,true,"Amazon · Fortgesetzt ab jetzt · "+evaluation.Summary);
                _repo.SetEnabled(job.Id,true);NotifyScheduleChanged();return;
            }
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
        if(IsGloballyPaused){_log("MarketWatcher ist global pausiert. Zuerst im Tray fortsetzen.");return;}
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
    private static Task CleanupProfileEdgeProcessesAsync()=>BrowserScanner.CleanupProfileProcessesAsync();
    private async Task<List<string>> SendNotificationsAsync(SearchJob job,IReadOnlyList<Listing> items)
    {
        var errors=new List<string>();
        if(IsGloballyPaused)return errors;
        try{await EmailSender.SendAsync(_repo.GetEmail(),job,items);}catch(Exception ex){errors.Add("E-Mail: "+ex.Message);}
        if(IsGloballyPaused)return errors;
        try{var tg=_repo.GetTelegram();var selected=job.TelegramRecipientIds.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);foreach(var recipient in tg.Recipients)recipient.Selected=selected.Contains(recipient.Id);await TelegramSender.SendAsync(tg,job,items);}catch(Exception ex){errors.Add("Telegram: "+ex.Message);}
        if(IsGloballyPaused)return errors;
        try
        {
            var wa=_repo.GetWhatsApp();var selected=job.RecipientIds.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var recipient in wa.Recipients)recipient.Selected=selected.Contains(recipient.Id);
            await SendWhatsAppConfiguredAsync(wa,job,items);
        }catch(Exception ex){errors.Add("WhatsApp: "+ex.Message);}
        return errors;
    }
    private async Task<List<string>> SendQueuedAsync(SearchJob job)
    {
        var errors=new List<string>();var pending=_repo.GetPending(job.Id);
        if(pending.Any(x=>x.Targets.Count==0))errors.Add("Keine aktiven Benachrichtigungsempfänger für wartende Treffer.");
        foreach(var target in pending.SelectMany(x=>x.Targets.Except(x.Delivered)).Distinct())
        {
            if(IsGloballyPaused)break;
            var batch=pending.Where(x=>x.Targets.Contains(target)&&!x.Delivered.Contains(target)).ToList();
            foreach(var delivery in batch)
            {
            if(IsGloballyPaused)break;
            try
            {
                var listings=new List<Listing>{delivery.Item};
                if(target=="email")await EmailSender.SendAsync(_repo.GetEmail(),job,listings);
                else if(target.StartsWith("wa:"))
                {
                    var wa=_repo.GetWhatsApp();var recipient=wa.Recipients.FirstOrDefault(x=>x.Id==target[3..]&&x.Enabled);
                    if(!wa.Enabled||recipient is null)throw new InvalidOperationException("WhatsApp-Empfänger deaktiviert oder entfernt.");
                    delivery.Attempts.TryGetValue(target,out var attempt);
                    if(attempt is not null&&!string.IsNullOrEmpty(attempt.MessageId))
                    {
                        if(attempt.NextAttemptUtc>DateTime.UtcNow)continue;
                        var phone=new string(recipient.Number.Where(char.IsDigit).ToArray());
                        var caption=wa.IncludeImages?NotificationText.BuildItem(job,delivery.Item):NotificationText.BuildChunks(job,listings,3000,false).Single();
                        var confirmed=await _scanner.IsWhatsAppMessageConfirmedAsync(phone,attempt.MessageId,caption,wa.IncludeImages&&!string.IsNullOrWhiteSpace(delivery.Item.ImageUrl));
                        if(!confirmed){_repo.FailDeliveryAttempt(delivery.Fingerprint,target,"Nachrichten-ID noch nicht bestätigt; Versand bleibt gesperrt.");errors.Add("WhatsApp: Status unklar, Wiederholung gesperrt.");continue;}
                        _repo.ConfirmDelivery(delivery.Fingerprint,target);
                        _log($"SUCHE #{job.Id} · bestehende WhatsApp-Nachrichten-ID bestätigt · keine erneute Sendung");
                        continue;
                    }
                    if(attempt is not null&&!attempt.CanSend(DateTime.UtcNow))
                    {
                        if(attempt.Status is "Preparing" or "NeedsReview")errors.Add("WhatsApp: Versand zur Prüfung zurückgehalten; automatische Wiederholung gesperrt.");
                        continue;
                    }
                    _repo.BeginDeliveryAttempt(delivery.Fingerprint,target);
                    wa.Recipients=[recipient];recipient.Selected=true;
                    await _scanner.SendWhatsAppAsync(wa,job,listings,id=>_repo.CheckpointMessageId(delivery.Fingerprint,target,id));
                }
                else if(target.StartsWith("tg:"))
                {
                    var tg=_repo.GetTelegram();var recipient=tg.Recipients.FirstOrDefault(x=>x.Id==target[3..]&&x.Enabled);
                    if(!tg.Enabled||recipient is null)throw new InvalidOperationException("Telegram-Empfänger deaktiviert oder entfernt.");
                    tg.Recipients=[recipient];recipient.Selected=true;await TelegramSender.SendAsync(tg,job,listings);
                }
                else throw new InvalidOperationException("Unbekannter Versandkanal.");
                _repo.ConfirmDelivery(delivery.Fingerprint,target);
                _log($"SUCHE #{job.Id} · Versand bestätigt · {target.Split(':')[0]} · {delivery.Item.Title}");
            }
            catch(Exception ex)
            {
                if(target.StartsWith("wa:")&&_repo.GetPending(job.Id).FirstOrDefault(x=>x.Fingerprint==delivery.Fingerprint)?.Attempts.ContainsKey(target)==true)
                    _repo.FailDeliveryAttempt(delivery.Fingerprint,target,ex.Message);
                errors.Add(target.Split(':')[0]+": "+ex.Message);
            }
            }
        }
        return errors;
    }
    public async Task TestWhatsAppAsync(WhatsAppSettings settings)
    {
        if(IsGloballyPaused)throw new InvalidOperationException("MarketWatcher ist global pausiert. Bitte zuerst im Tray fortsetzen.");
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
