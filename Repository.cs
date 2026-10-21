using System.Text.Json;
using System.IO;

namespace MarketWatcher;

public sealed class Repository(string stateFile)
{
    private readonly object _sync=new();
    private readonly JsonSerializerOptions _json=new(){WriteIndented=true};
    private PortableState _state=new();
    public bool RequiresReauthentication { get; private set; }
    private static string EnvironmentId=>$"{Environment.MachineName}|{Environment.UserDomainName}|{Environment.UserName}";

    public void Initialize()
    {
        lock(_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);Directory.CreateDirectory(AppPaths.BackupDirectory);
            _state=LoadState();
            if(_state.WhatsAppDeliveryVersion<1)
            {
                foreach(var pending in _state.Outbox)
                foreach(var target in pending.Targets.Where(x=>x.StartsWith("wa:")&&!pending.Delivered.Contains(x)))
                    pending.Attempts[target]=new(){Status="NeedsReview",LastError="Alter Versandstatus unklar. Automatische Wiederholung gesperrt."};
                _state.WhatsAppDeliveryVersion=1;
            }
            foreach(var job in _state.Jobs.Where(x=>x.LastNewResultUtc is null))
            {
                var seen=_state.SeenItems.Where(x=>x.JobId==job.Id).ToList();
                var notified=seen.Select(x=>ParseUtc(x.NotifiedUtc)).Where(x=>x.HasValue).Select(x=>x!.Value).ToList();
                var firstSeen=seen.Select(x=>ParseUtc(x.FirstSeenUtc)).Where(x=>x.HasValue).Select(x=>x!.Value).ToList();
                job.LastNewResultUtc=notified.Count>0?notified.Max():firstSeen.Count>0?firstSeen.Max():job.LastRunUtc??DateTime.UtcNow;
            }
            if(string.IsNullOrWhiteSpace(_state.EnvironmentId))_state.EnvironmentId=EnvironmentId;
            else if(!string.Equals(_state.EnvironmentId,EnvironmentId,StringComparison.Ordinal))
            {
                RequiresReauthentication=true;_state.EnvironmentId=EnvironmentId;
                _state.Email.ProtectedPassword="";_state.Telegram.ProtectedBotToken="";
            }
            SaveLocked();
        }
    }
    private PortableState LoadState()
    {
        var candidates=new[]{stateFile,Path.Combine(AppPaths.BackupDirectory,"state-latest.json")}
            .Concat(Directory.EnumerateFiles(AppPaths.BackupDirectory,"state-history-*.json").OrderByDescending(File.GetLastWriteTimeUtc)).ToList();
        var existing=candidates.Where(File.Exists).ToList();var failures=new List<string>();
        foreach(var candidate in existing)
            try
            {
                var loaded=JsonSerializer.Deserialize<PortableState>(File.ReadAllText(candidate),_json);
                if(loaded is null||loaded.FormatVersion!=1)throw new InvalidDataException("Unbekanntes oder leeres Datenformat.");
                if(candidate!=stateFile&&File.Exists(stateFile))File.Copy(stateFile,stateFile+".unreadable-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"),false);
                return loaded;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException){failures.Add(Path.GetFileName(candidate)+": "+ex.Message);}
        if(existing.Count>0)throw new InvalidDataException("Suchdaten konnten nicht geladen werden. Die Dateien werden nicht überschrieben. "+string.Join(" | ",failures));
        return new();
    }
    private static DateTime? ParseUtc(string? value)=>DateTime.TryParse(value,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out var parsed)?parsed.ToUniversalTime():null;
    public List<SearchJob> GetJobs(){lock(_sync)return _state.Jobs.OrderBy(x=>x.Id).Select(Clone).ToList();}
    public SearchJob? GetJob(long id)=>GetJobs().FirstOrDefault(x=>x.Id==id);
    public long AddJob(SearchJob job){lock(_sync){job.Id=_state.NextJobId++;_state.Jobs.Add(Clone(job));SaveLocked();return job.Id;}}
    public void SetEnabled(long id,bool value)=>ChangeJob(id,x=>x.Enabled=value);
    public void UpdateSchedule(long id,int seconds,bool enabled)=>ChangeJob(id,x=>{x.IntervalSeconds=seconds;x.Enabled=enabled;});
    public void UpdateMatchMode(long id,string mode)=>ChangeJob(id,x=>x.MatchMode=mode);
    public void UpdateAmazon(long id,AmazonWatchSettings settings,int interval)=>ChangeJob(id,x=>{x.Amazon=settings;x.IntervalSeconds=interval;x.AmazonConditions.Clear();});
    public void UpdateAmazonObservation(long id,Dictionary<string,bool> conditions)=>ChangeJob(id,x=>x.AmazonConditions=conditions);
    public void UpdateAmazonTitle(long id,string title)=>ChangeJob(id,x=>{if(x.Name.StartsWith("Amazon · ",StringComparison.Ordinal))x.Name=title;});
    public void UpdateRecipients(long id,bool first,bool second)=>ChangeJob(id,x=>{x.SendToFirst=first;x.SendToSecond=second;});
    public void UpdateRecipients(long id,string ids)=>ChangeJob(id,x=>x.RecipientIds=ids);
    public void UpdateRecipients(long id,string whatsappIds,string telegramIds)=>ChangeJob(id,x=>{x.RecipientIds=whatsappIds;x.TelegramRecipientIds=telegramIds;});
    public void DeleteJob(long id){lock(_sync){_state.Jobs.RemoveAll(x=>x.Id==id);SaveLocked();}}
    public void UpdateRun(long id,bool initialized,string status)=>ChangeJob(id,x=>{x.Initialized=initialized;x.LastRunUtc=DateTime.UtcNow;x.Status=status;});
    public void SetLastNewResult(long id,DateTime utc)=>ChangeJob(id,x=>x.LastNewResultUtc=utc);
    private void ChangeJob(long id,Action<SearchJob> change){lock(_sync){var job=_state.Jobs.FirstOrDefault(x=>x.Id==id);if(job is null)return;change(job);SaveLocked();}}
    public bool AddSeen(long jobId,string fingerprint,Listing item,bool notified)
    {
        lock(_sync){if(_state.SeenItems.Any(x=>x.Fingerprint==fingerprint))return false;_state.SeenItems.Add(new(){Fingerprint=fingerprint,JobId=jobId,ExternalId=item.ExternalId,Title=item.Title,Url=item.Url,Price=item.Price,FirstSeenUtc=DateTime.UtcNow.ToString("O"),NotifiedUtc=null});if(notified)QueueLocked(jobId,fingerprint,item);SaveLocked(false);return true;}
    }
    private void QueueLocked(long jobId,string fingerprint,Listing item)
    {
        if(_state.Outbox.Any(x=>x.Fingerprint==fingerprint))return;
        var job=_state.Jobs.First(x=>x.Id==jobId);var targets=new List<string>();
        if(!string.IsNullOrWhiteSpace(_state.Email.User)&&!string.IsNullOrWhiteSpace(_state.Email.Recipient))targets.Add("email");
        var wa=job.RecipientIds.Split(',').ToHashSet();var tg=job.TelegramRecipientIds.Split(',').ToHashSet();
        if(_state.WhatsApp.Enabled)targets.AddRange(_state.WhatsApp.Recipients.Where(x=>x.Enabled&&wa.Contains(x.Id)).Select(x=>"wa:"+x.Id));
        if(_state.Telegram.Enabled)targets.AddRange(_state.Telegram.Recipients.Where(x=>x.Enabled&&tg.Contains(x.Id)).Select(x=>"tg:"+x.Id));
        _state.Outbox.Add(new(){JobId=jobId,Fingerprint=fingerprint,Item=item,Targets=targets});
    }
    public List<PendingDelivery> GetPending(long jobId){lock(_sync)return RoundTrip(_state.Outbox.Where(x=>x.JobId==jobId).ToList());}
    public void BeginDeliveryAttempt(string fingerprint,string target)
    {
        lock(_sync)
        {
            var pending=_state.Outbox.Single(x=>x.Fingerprint==fingerprint);
            if(!pending.Attempts.TryGetValue(target,out var attempt))pending.Attempts[target]=attempt=new();
            if(!attempt.CanSend(DateTime.UtcNow))throw new InvalidOperationException("Automatische Wiederholung gesperrt: "+attempt.Status);
            attempt.Count++;attempt.Status="Preparing";attempt.NextAttemptUtc=null;SaveLocked(false);
        }
    }
    public void CheckpointMessageId(string fingerprint,string target,string messageId)
    {
        if(string.IsNullOrWhiteSpace(messageId))throw new InvalidOperationException("WhatsApp-Nachrichten-ID fehlt.");
        lock(_sync)
        {
            var pending=_state.Outbox.Single(x=>x.Fingerprint==fingerprint);
            var attempt=pending.Attempts[target];attempt.MessageId=messageId;attempt.Status="AwaitingConfirmation";SaveLocked(false);
        }
    }
    public void FailDeliveryAttempt(string fingerprint,string target,string detail)
    {
        lock(_sync)
        {
            var pending=_state.Outbox.Single(x=>x.Fingerprint==fingerprint);var attempt=pending.Attempts[target];
            attempt.LastError=detail;
            attempt.Status=!string.IsNullOrEmpty(attempt.MessageId)?"AwaitingConfirmation":attempt.Count>=3?"NeedsReview":"Retry";
            attempt.NextAttemptUtc=DateTime.UtcNow.AddMinutes(Math.Pow(2,Math.Min(attempt.Count,5)));
            SaveLocked(false);
        }
    }
    public void ConfirmDelivery(string fingerprint,string target)
    {
        lock(_sync)
        {
            var pending=_state.Outbox.FirstOrDefault(x=>x.Fingerprint==fingerprint);if(pending is null)return;
            if(pending.Attempts.TryGetValue(target,out var attempt)&&!string.IsNullOrEmpty(attempt.MessageId)&&!_state.DeliveryReceipts.Any(x=>x.MessageId==attempt.MessageId&&x.Target==target))
                _state.DeliveryReceipts.Add(new(){Fingerprint=fingerprint,Target=target,MessageId=attempt.MessageId,ConfirmedUtc=DateTime.UtcNow});
            if(!pending.Delivered.Contains(target))pending.Delivered.Add(target);
            if(pending.Targets.Count>0&&pending.Targets.All(pending.Delivered.Contains))
            {
                var seen=_state.SeenItems.FirstOrDefault(x=>x.Fingerprint==fingerprint);if(seen is not null)seen.NotifiedUtc=DateTime.UtcNow.ToString("O");
                _state.Outbox.Remove(pending);
            }
            SaveLocked(false);
        }
    }
    public void Requeue(long jobId,Listing item)
    {
        lock(_sync){var fp=BrowserScanner.Fingerprint(_state.Jobs.First(x=>x.Id==jobId).Platform,item);QueueLocked(jobId,fp,item);var seen=_state.SeenItems.FirstOrDefault(x=>x.Fingerprint==fp);if(seen is not null)seen.NotifiedUtc=null;SaveLocked();}
    }
    public void QueueAmazonNotification(long jobId,Listing item,Dictionary<string,bool> conditions)
    {
        lock(_sync)
        {
            QueueLocked(jobId,"amazon-event-"+Guid.NewGuid().ToString("N"),item);
            _state.Jobs.Single(x=>x.Id==jobId).AmazonConditions=conditions;
            SaveLocked();
        }
    }
    public EmailSettings GetEmail(){lock(_sync)return RoundTrip(_state.Email);}
    public void SaveEmail(EmailSettings value){lock(_sync){_state.Email=RoundTrip(value);SaveLocked();}}
    public TelegramSettings GetTelegram(){lock(_sync)return RoundTrip(_state.Telegram);}
    public void SaveTelegram(TelegramSettings value){lock(_sync){_state.Telegram=RoundTrip(value);SaveLocked();}}
    public WhatsAppSettings GetWhatsApp(){lock(_sync)return RoundTrip(_state.WhatsApp);}
    public void SaveWhatsApp(WhatsAppSettings value){lock(_sync){_state.WhatsApp=RoundTrip(value);SaveLocked();}}
    public GeneralSettings GetGeneral(){lock(_sync)return RoundTrip(_state.General);}
    public void SaveGeneral(GeneralSettings value){lock(_sync){_state.General.KeepBrowserOpen=value.KeepBrowserOpen;SaveLocked();}}
    public void SetGlobalPause(bool paused,bool includeMissed=true)
    {
        lock(_sync)
        {
            _state.General.GloballyPaused=paused;
            _state.General.PausedSinceUtc=paused?DateTime.UtcNow:null;
            if(!paused)foreach(var job in _state.Jobs.Where(x=>x.Enabled)){job.SkipBacklogOnce=!includeMissed;job.LastRunUtc=null;}
            SaveLocked();
        }
    }
    public void CompleteBacklogBaseline(long id)=>ChangeJob(id,x=>x.SkipBacklogOnce=false);
    public RuntimeStatistics GetStatistics(){lock(_sync)return RoundTrip(_state.Statistics);}
    public void RecordSuccessfulNotifications(int count){if(count<=0)return;lock(_sync){_state.Statistics.SuccessfulNotifications+=count;SaveLocked();}}
    public void RecordErrors(int count=1){if(count<=0)return;lock(_sync){_state.Statistics.Errors+=count;SaveLocked();}}
    public void BackupNow(){lock(_sync)SaveLocked();}
    private void SaveLocked(bool backup=true)
    {
        foreach(var job in _state.Jobs)job.SeenCount=_state.SeenItems.Count(x=>x.JobId==job.Id);
        var text=JsonSerializer.Serialize(_state,_json);var temp=stateFile+".tmp";File.WriteAllText(temp,text);
        if(backup&&File.Exists(stateFile))
        {
            var history=Path.Combine(AppPaths.BackupDirectory,"state-history-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+"-"+Guid.NewGuid().ToString("N")+".json");
            File.Copy(stateFile,history,false);
        }
        File.Move(temp,stateFile,true);
        if(backup){var backupFile=Path.Combine(AppPaths.BackupDirectory,"state-latest.json");File.Copy(stateFile,backupFile,true);}
    }
    private T RoundTrip<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value,_json),_json)!;
    private SearchJob Clone(SearchJob value)=>RoundTrip(value);
}

public sealed class PortableState
{
    public int FormatVersion { get; set; }=1;
    public string EnvironmentId { get; set; }="";
    public long NextJobId { get; set; }=1;
    public List<SearchJob> Jobs { get; set; }=[];
    public List<PortableSeenItem> SeenItems { get; set; }=[];
    public EmailSettings Email { get; set; }=new();
    public TelegramSettings Telegram { get; set; }=new();
    public WhatsAppSettings WhatsApp { get; set; }=new();
    public GeneralSettings General { get; set; }=new();
    public RuntimeStatistics Statistics { get; set; }=new();
    public List<PendingDelivery> Outbox { get; set; }=[];
    public int WhatsAppDeliveryVersion { get; set; }
    public List<ConfirmedDelivery> DeliveryReceipts {get;set;}=[];
}
public sealed class ConfirmedDelivery
{
    public string Fingerprint {get;set;}="";
    public string Target {get;set;}="";
    public string MessageId {get;set;}="";
    public DateTime ConfirmedUtc {get;set;}
}
public sealed class PendingDelivery
{
    public long JobId {get;set;}
    public string Fingerprint {get;set;}="";
    public Listing Item {get;set;}=new("","","","");
    public List<string> Targets {get;set;}=[];
    public List<string> Delivered {get;set;}=[];
    public Dictionary<string,DeliveryAttempt> Attempts {get;set;}=new();
}
public sealed class DeliveryAttempt
{
    public string Status {get;set;}="Ready";
    public int Count {get;set;}
    public string MessageId {get;set;}="";
    public DateTime? NextAttemptUtc {get;set;}
    public string LastError {get;set;}="";
    public bool CanSend(DateTime utc)=>string.IsNullOrEmpty(MessageId)&&(Status is "Ready" or "Retry")&&Count<3&&(!NextAttemptUtc.HasValue||utc>=NextAttemptUtc.Value);
}
public sealed class PortableSeenItem
{
    public string Fingerprint { get; set; }="";public long JobId { get; set; }public string ExternalId { get; set; }="";public string Title { get; set; }="";public string Url { get; set; }="";public string Price { get; set; }="";public string FirstSeenUtc { get; set; }="";public string? NotifiedUtc { get; set; }
}
