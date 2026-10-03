using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace MarketWatcher;

public sealed class Repository(string database)
{
    private string Cs => $"Data Source={database}";
    public void Initialize()
    {
        using var db = new SqliteConnection(Cs); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS jobs(id INTEGER PRIMARY KEY, name TEXT NOT NULL, url TEXT NOT NULL, platform TEXT NOT NULL, interval_seconds INTEGER NOT NULL, enabled INTEGER NOT NULL, initialized INTEGER NOT NULL DEFAULT 0, last_run_utc TEXT, status TEXT NOT NULL DEFAULT 'Neu', match_mode TEXT NOT NULL DEFAULT 'title', send_to_first INTEGER NOT NULL DEFAULT 1, send_to_second INTEGER NOT NULL DEFAULT 1, recipient_ids TEXT NOT NULL DEFAULT 'recipient-1,recipient-2', telegram_recipient_ids TEXT NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS seen_items(id INTEGER PRIMARY KEY, fingerprint TEXT NOT NULL UNIQUE, job_id INTEGER NOT NULL, external_id TEXT, title TEXT, url TEXT, price TEXT, first_seen_utc TEXT NOT NULL, notified_utc TEXT);
        CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """; cmd.ExecuteNonQuery();
        try{using var migration=db.CreateCommand();migration.CommandText="ALTER TABLE jobs ADD COLUMN match_mode TEXT NOT NULL DEFAULT 'title'";migration.ExecuteNonQuery();}catch(SqliteException ex)when(ex.SqliteErrorCode==1){}
        try{using var migration=db.CreateCommand();migration.CommandText="ALTER TABLE jobs ADD COLUMN send_to_first INTEGER NOT NULL DEFAULT 1";migration.ExecuteNonQuery();}catch(SqliteException ex)when(ex.SqliteErrorCode==1){}
        try{using var migration=db.CreateCommand();migration.CommandText="ALTER TABLE jobs ADD COLUMN send_to_second INTEGER NOT NULL DEFAULT 1";migration.ExecuteNonQuery();}catch(SqliteException ex)when(ex.SqliteErrorCode==1){}
        try{using var migration=db.CreateCommand();migration.CommandText="ALTER TABLE jobs ADD COLUMN recipient_ids TEXT NOT NULL DEFAULT 'recipient-1,recipient-2'";migration.ExecuteNonQuery();}catch(SqliteException ex)when(ex.SqliteErrorCode==1){}
        try{using var migration=db.CreateCommand();migration.CommandText="ALTER TABLE jobs ADD COLUMN telegram_recipient_ids TEXT NOT NULL DEFAULT ''";migration.ExecuteNonQuery();}catch(SqliteException ex)when(ex.SqliteErrorCode==1){}
    }

    public List<SearchJob> GetJobs()
    {
        using var db = new SqliteConnection(Cs); db.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT j.id,j.name,j.url,j.platform,j.interval_seconds,j.enabled,j.initialized,j.last_run_utc,j.status,COALESCE(j.match_mode,'title'),COALESCE(j.send_to_first,1),COALESCE(j.send_to_second,1),COALESCE(j.recipient_ids,''),COALESCE(j.telegram_recipient_ids,''),(SELECT COUNT(*) FROM seen_items s WHERE s.job_id=j.id) seen FROM jobs j ORDER BY j.id DESC";
        using var r = cmd.ExecuteReader(); var list = new List<SearchJob>();
        while (r.Read()) list.Add(new SearchJob { Id=r.GetInt64(0), Name=r.GetString(1), Url=r.GetString(2), Platform=r.GetString(3), IntervalSeconds=r.GetInt32(4), Enabled=r.GetInt32(5)!=0, Initialized=r.GetInt32(6)!=0, LastRunUtc=r.IsDBNull(7)?null:DateTime.Parse(r.GetString(7),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime(), Status=r.GetString(8), MatchMode=r.GetString(9), SendToFirst=r.GetInt32(10)!=0, SendToSecond=r.GetInt32(11)!=0, RecipientIds=r.GetString(12), TelegramRecipientIds=r.GetString(13), SeenCount=r.GetInt32(14) });
        return list;
    }
    public SearchJob? GetJob(long id) => GetJobs().FirstOrDefault(x => x.Id == id);
    public long AddJob(SearchJob j) { using var db=new SqliteConnection(Cs); db.Open(); using var c=db.CreateCommand(); c.CommandText="INSERT INTO jobs(name,url,platform,interval_seconds,enabled,status,match_mode,send_to_first,send_to_second,recipient_ids,telegram_recipient_ids) VALUES($n,$u,$p,$i,1,$s,$m,$f,$q,$r,$t); SELECT last_insert_rowid();"; c.Parameters.AddWithValue("$n",j.Name);c.Parameters.AddWithValue("$u",j.Url);c.Parameters.AddWithValue("$p",j.Platform);c.Parameters.AddWithValue("$i",j.IntervalSeconds);c.Parameters.AddWithValue("$s",j.Status);c.Parameters.AddWithValue("$m",j.MatchMode);c.Parameters.AddWithValue("$f",j.SendToFirst?1:0);c.Parameters.AddWithValue("$q",j.SendToSecond?1:0);c.Parameters.AddWithValue("$r",j.RecipientIds);c.Parameters.AddWithValue("$t",j.TelegramRecipientIds);return (long)c.ExecuteScalar()!; }
    public void SetEnabled(long id,bool enabled)=>Exec("UPDATE jobs SET enabled=$v WHERE id=$id",("$v",enabled?1:0),("$id",id));
    public void UpdateSchedule(long id,int intervalSeconds,bool enabled)=>Exec("UPDATE jobs SET interval_seconds=$i,enabled=$e WHERE id=$id",("$i",intervalSeconds),("$e",enabled?1:0),("$id",id));
    public void UpdateMatchMode(long id,string mode)=>Exec("UPDATE jobs SET match_mode=$m WHERE id=$id",("$m",mode),("$id",id));
    public void UpdateRecipients(long id,bool first,bool second)=>Exec("UPDATE jobs SET send_to_first=$f,send_to_second=$s WHERE id=$id",("$f",first?1:0),("$s",second?1:0),("$id",id));
    public void UpdateRecipients(long id,string recipientIds)=>Exec("UPDATE jobs SET recipient_ids=$r WHERE id=$id",("$r",recipientIds),("$id",id));
    public void UpdateRecipients(long id,string whatsappIds,string telegramIds)=>Exec("UPDATE jobs SET recipient_ids=$w,telegram_recipient_ids=$t WHERE id=$id",("$w",whatsappIds),("$t",telegramIds),("$id",id));
    public void DeleteJob(long id)=>Exec("DELETE FROM jobs WHERE id=$id",("$id",id));
    public void UpdateRun(long id,bool initialized,string status)=>Exec("UPDATE jobs SET initialized=$i,last_run_utc=$t,status=$s WHERE id=$id",("$i",initialized?1:0),("$t",DateTime.UtcNow.ToString("O")),("$s",status),("$id",id));
    public bool AddSeen(long jobId,string fingerprint,Listing item,bool notified)
    {
        using var db=new SqliteConnection(Cs);db.Open();using var c=db.CreateCommand();c.CommandText="INSERT OR IGNORE INTO seen_items(fingerprint,job_id,external_id,title,url,price,first_seen_utc,notified_utc) VALUES($f,$j,$e,$t,$u,$p,$d,$n)";
        c.Parameters.AddWithValue("$f",fingerprint);c.Parameters.AddWithValue("$j",jobId);c.Parameters.AddWithValue("$e",item.ExternalId);c.Parameters.AddWithValue("$t",item.Title);c.Parameters.AddWithValue("$u",item.Url);c.Parameters.AddWithValue("$p",item.Price);c.Parameters.AddWithValue("$d",DateTime.UtcNow.ToString("O"));c.Parameters.AddWithValue("$n",notified?DateTime.UtcNow.ToString("O"):DBNull.Value);return c.ExecuteNonQuery()>0;
    }
    public EmailSettings GetEmail() { var json=Scalar("SELECT value FROM settings WHERE key='email'"); return json is null?new():JsonSerializer.Deserialize<EmailSettings>(json)??new(); }
    public void SaveEmail(EmailSettings s)=>Exec("INSERT INTO settings(key,value) VALUES('email',$v) ON CONFLICT(key) DO UPDATE SET value=$v",("$v",JsonSerializer.Serialize(s)));
    public TelegramSettings GetTelegram() { var json=Scalar("SELECT value FROM settings WHERE key='telegram'");var value=json is null?new TelegramSettings():JsonSerializer.Deserialize<TelegramSettings>(json)??new();if(value.Recipients.Count==0&&!string.IsNullOrWhiteSpace(value.ChatId))value.Recipients.Add(new(){Id="telegram-1",Name="Telegram",ChatId=value.ChatId,Enabled=true});return value; }
    public void SaveTelegram(TelegramSettings s)=>Exec("INSERT INTO settings(key,value) VALUES('telegram',$v) ON CONFLICT(key) DO UPDATE SET value=$v",("$v",JsonSerializer.Serialize(s)));
    public WhatsAppSettings GetWhatsApp()
    {
        var json=Scalar("SELECT value FROM settings WHERE key='whatsapp'");var value=json is null?new WhatsAppSettings():JsonSerializer.Deserialize<WhatsAppSettings>(json)??new();
        if(value.Recipients.Count==0)
        {
            if(!string.IsNullOrWhiteSpace(value.RecipientNumber))value.Recipients.Add(new(){Id="recipient-1",Name=value.RecipientName,Number=value.RecipientNumber,Enabled=value.SendToFirst});
            if(!string.IsNullOrWhiteSpace(value.Recipient2Number))value.Recipients.Add(new(){Id="recipient-2",Name=value.Recipient2Name,Number=value.Recipient2Number,Enabled=value.SendToSecond});
        }
        return value;
    }
    public void SaveWhatsApp(WhatsAppSettings s)=>Exec("INSERT INTO settings(key,value) VALUES('whatsapp',$v) ON CONFLICT(key) DO UPDATE SET value=$v",("$v",JsonSerializer.Serialize(s)));
    private string? Scalar(string sql){using var db=new SqliteConnection(Cs);db.Open();using var c=db.CreateCommand();c.CommandText=sql;return c.ExecuteScalar() as string;}
    private void Exec(string sql,params (string,object)[] args){using var db=new SqliteConnection(Cs);db.Open();using var c=db.CreateCommand();c.CommandText=sql;foreach(var a in args)c.Parameters.AddWithValue(a.Item1,a.Item2);c.ExecuteNonQuery();}
}
