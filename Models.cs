using System.IO;

namespace MarketWatcher;

public sealed class GeneralSettings
{
    public bool KeepBrowserOpen { get; set; } = true;
}

public sealed class SearchJob
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Platform { get; set; } = "Andere";
    public int IntervalSeconds { get; set; } = 60;
    public bool Enabled { get; set; } = true;
    public bool Initialized { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public DateTime? LastNewResultUtc { get; set; }
    public string Status { get; set; } = "Neu";
    public string MatchMode { get; set; } = "title";
    public bool SendToFirst { get; set; } = true;
    public bool SendToSecond { get; set; } = true;
    public string RecipientIds { get; set; } = "recipient-1,recipient-2";
    public string TelegramRecipientIds { get; set; } = "";
    public int SeenCount { get; set; }
    public AmazonWatchSettings Amazon { get; set; } = new();
    public Dictionary<string,bool> AmazonConditions { get; set; } = new();
    public string SearchNumber => $"#{Id}";
    public string ActivityGroup => Enabled ? "Aktive Suchen" : "Pausierte Suchen";
    public string IntervalLabel => $"{IntervalSeconds} s";
    public string MatchModeLabel => Platform=="Amazon" ? "Preis / Lieferbarkeit / Prime" : MatchMode == "title_or_content" ? "Titel oder Inhalt" : "Nur Titel";
    public string RecipientsLabel => RecipientIds.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Length switch {0=>"Keine",1=>"1 Person",var n=>$"{n} Personen"};
    public string LastRunLabel => LastRunUtc?.ToLocalTime().ToString("dd.MM. HH:mm:ss") ?? "–";
    public string PortalLogoPath
    {
        get
        {
            var file=Platform.ToLowerInvariant() switch{"amazon"=>"Amazon.png","willhaben"=>"willhaben.png","kleinanzeigen"=>"kleinanzeigen.png","vinted"=>"Vinted.png","markt.de"=>"markt.de.png","quoka"=>"Quoka.png","tutti"=>"Tutti.png",var x when x.StartsWith("ebay")=>"eBay.png",_=>""};
            return string.IsNullOrEmpty(file)?"":Path.Combine(AppContext.BaseDirectory,"Assets","Portals",file);
        }
    }
}

public sealed class AmazonWatchSettings
{
    public bool NotifyAvailable { get; set; }
    public bool WatchNewPrice { get; set; } = true;
    public decimal? NewBelow { get; set; }
    public bool WatchUsed { get; set; } = true;
    public decimal? UsedBelow { get; set; }
    public bool NotifyPrime { get; set; } = true;
    public bool OnlyAmazonResale { get; set; }
}

public sealed class AmazonObservation
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public List<AmazonOffer> Offers { get; set; } = [];
}
public sealed class AmazonOffer
{
    public decimal? Price { get; set; }
    public bool Used { get; set; }
    public bool Prime { get; set; }
    public bool Resale { get; set; }
}

public sealed class RuntimeStatistics
{
    public long SuccessfulNotifications { get; set; }
    public long Errors { get; set; }
}

public sealed record Listing(string ExternalId, string Title, string Url, string Price, string ImageUrl = "", string PostalCode = "", string Location = "", string SearchText = "");

public sealed class EmailSettings
{
    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public bool IncludeImages { get; set; } = true;
}

public sealed class TelegramSettings
{
    public bool Enabled { get; set; }
    public string ChatId { get; set; } = "";
    public string ProtectedBotToken { get; set; } = "";
    public List<TelegramRecipient> Recipients { get; set; } = [];
    public bool IncludeImages { get; set; } = true;
}

public sealed class TelegramRecipient
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Neuer Telegram-Chat";
    public string ChatId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Selected { get; set; }
}

public sealed class WhatsAppSettings
{
    public bool Enabled { get; set; }
    public bool UseDesktopApp { get; set; }
    public bool SendToFirst { get; set; } = true;
    public string RecipientName { get; set; } = "Ich selbst";
    public string RecipientNumber { get; set; } = "";
    public bool SendToSecond { get; set; }
    public string Recipient2Name { get; set; } = "";
    public string Recipient2Number { get; set; } = "";
    public List<WhatsAppRecipient> Recipients { get; set; } = [];
    public bool IncludeImages { get; set; } = true;
}

public sealed class WhatsAppRecipient
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Neue Person";
    public string Number { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Selected { get; set; }
}
