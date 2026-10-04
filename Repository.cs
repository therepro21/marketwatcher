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
        foreach(var candidate in new[]{stateFile,Path.Combine(AppPaths.BackupDirectory,"state-latest.json")})
            try{if(File.Exists(candidate))return JsonSerializer.Deserialize<PortableState>(File.ReadAllText(candidate),_json)??new();}catch{}
        return new();
    }
    private static DateTime? ParseUtc(string? value)=>DateTime.TryParse(value,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out var parsed)?parsed.ToUniversalTime():null;
    public List<SearchJob> GetJobs(){lock(_sync)return _state.Jobs.OrderByDescending(x=>x.Id).Select(Clone).ToList();}
    public SearchJob? GetJob(long id)=>GetJobs().FirstOrDefault(x=>x.Id==id);
    public long AddJob(SearchJob job){lock(_sync){job.Id=_state.NextJobId++;_state.Jobs.Add(Clone(job));SaveLocked();return job.Id;}}
    public void SetEnabled(long id,bool value)=>ChangeJob(id,x=>x.Enabled=value);
    public void UpdateSchedule(long id,int seconds,bool enabled)=>ChangeJob(id,x=>{x.IntervalSeconds=seconds;x.Enabled=enabled;});
    public void UpdateMatchMode(long id,string mode)=>ChangeJob(id,x=>x.MatchMode=mode);
    public void UpdateRecipients(long id,bool first,bool second)=>ChangeJob(id,x=>{x.SendToFirst=first;x.SendToSecond=second;});
    public void UpdateRecipients(long id,string ids)=>ChangeJob(id,x=>x.RecipientIds=ids);
    public void UpdateRecipients(long id,string whatsappIds,string telegramIds)=>ChangeJob(id,x=>{x.RecipientIds=whatsappIds;x.TelegramRecipientIds=telegramIds;});
    public void DeleteJob(long id){lock(_sync){_state.Jobs.RemoveAll(x=>x.Id==id);SaveLocked();}}
    public void UpdateRun(long id,bool initialized,string status)=>ChangeJob(id,x=>{x.Initialized=initialized;x.LastRunUtc=DateTime.UtcNow;x.Status=status;});
    public void SetLastNewResult(long id,DateTime utc)=>ChangeJob(id,x=>x.LastNewResultUtc=utc);
    private void ChangeJob(long id,Action<SearchJob> change){lock(_sync){var job=_state.Jobs.FirstOrDefault(x=>x.Id==id);if(job is null)return;change(job);SaveLocked();}}
    public bool AddSeen(long jobId,string fingerprint,Listing item,bool notified)
    {
        lock(_sync){if(_state.SeenItems.Any(x=>x.Fingerprint==fingerprint))return false;_state.SeenItems.Add(new(){Fingerprint=fingerprint,JobId=jobId,ExternalId=item.ExternalId,Title=item.Title,Url=item.Url,Price=item.Price,FirstSeenUtc=DateTime.UtcNow.ToString("O"),NotifiedUtc=notified?DateTime.UtcNow.ToString("O"):null});SaveLocked(false);return true;}
    }
    public EmailSettings GetEmail(){lock(_sync)return RoundTrip(_state.Email);}
    public void SaveEmail(EmailSettings value){lock(_sync){_state.Email=RoundTrip(value);SaveLocked();}}
    public TelegramSettings GetTelegram(){lock(_sync)return RoundTrip(_state.Telegram);}
    public void SaveTelegram(TelegramSettings value){lock(_sync){_state.Telegram=RoundTrip(value);SaveLocked();}}
    public WhatsAppSettings GetWhatsApp(){lock(_sync)return RoundTrip(_state.WhatsApp);}
    public void SaveWhatsApp(WhatsAppSettings value){lock(_sync){_state.WhatsApp=RoundTrip(value);SaveLocked();}}
    public GeneralSettings GetGeneral(){lock(_sync)return RoundTrip(_state.General);}
    public void SaveGeneral(GeneralSettings value){lock(_sync){_state.General=RoundTrip(value);SaveLocked();}}
    public void BackupNow(){lock(_sync)SaveLocked();}
    private void SaveLocked(bool backup=true)
    {
        foreach(var job in _state.Jobs)job.SeenCount=_state.SeenItems.Count(x=>x.JobId==job.Id);
        var text=JsonSerializer.Serialize(_state,_json);var temp=stateFile+".tmp";File.WriteAllText(temp,text);File.Move(temp,stateFile,true);
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
}
public sealed class PortableSeenItem
{
    public string Fingerprint { get; set; }="";public long JobId { get; set; }public string ExternalId { get; set; }="";public string Title { get; set; }="";public string Url { get; set; }="";public string Price { get; set; }="";public string FirstSeenUtc { get; set; }="";public string? NotifiedUtc { get; set; }
}
