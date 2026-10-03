namespace MarketWatcher;

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
    public string SearchNumber => $"#{Id}";
    public string IntervalLabel => $"{IntervalSeconds} s";
    public string MatchModeLabel => MatchMode == "title_or_content" ? "Titel oder Inhalt" : "Nur Titel";
    public string RecipientsLabel => RecipientIds.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Length switch {0=>"Keine",1=>"1 Person",var n=>$"{n} Personen"};
    public string LastRunLabel => LastRunUtc?.ToLocalTime().ToString("dd.MM. HH:mm:ss") ?? "–";
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
