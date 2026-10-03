using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MarketWatcher;

public static class TelegramSender
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task SendAsync(TelegramSettings settings, SearchJob job, IReadOnlyList<Listing> items)
    {
        if (!settings.Enabled) return;
        var token = SecretStore.Unprotect(settings.ProtectedBotToken);
        var recipients=settings.Recipients.Where(x=>x.Enabled&&x.Selected).ToList();
        if (string.IsNullOrWhiteSpace(token) || recipients.Count==0)
            throw new InvalidOperationException("Telegram ist aktiviert, aber Bot-Token oder ausgewählter Chat fehlt.");
        foreach(var recipient in recipients)
        foreach (var message in NotificationText.BuildChunks(job, items, 3900,settings.IncludeImages))
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { chat_id=recipient.ChatId, text=message, disable_web_page_preview=false }), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Telegram meldet {(int)response.StatusCode}: {detail}");
            }
        }
    }

    public static async Task TestAsync(TelegramSettings settings)
    {
        var job = new SearchJob { Name="Testsuche", Platform="MarketWatcher" };
        await SendAsync(settings.withEnabled(), job, [new Listing("test", "Testnachricht erfolgreich", "https://example.com", "")]);
    }
    private static TelegramSettings withEnabled(this TelegramSettings s) { foreach(var x in s.Recipients)x.Selected=x.Enabled;return new(){Enabled=true,ChatId=s.ChatId,ProtectedBotToken=s.ProtectedBotToken,Recipients=s.Recipients,IncludeImages=s.IncludeImages}; }
}

public static class NotificationText
{
    public static IEnumerable<string> BuildChunks(SearchJob job, IReadOnlyList<Listing> items, int maxLength,bool includeImages=false)
    {
        var header=$"🆕 {items.Count} neue Treffer – {job.Name}\n"; var current=new StringBuilder(header);
        foreach(var item in items)
        {
            var details=new[]{item.Price,item.PostalCode,item.Location,job.Platform}.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
            var image=includeImages&&!string.IsNullOrWhiteSpace(item.ImageUrl)?$"\n🖼 {item.ImageUrl}":"";
            var block=$"\n{item.Title}\n{string.Join(" · ",details)}{image}\n{item.Url}\n";
            if(current.Length+block.Length>maxLength && current.Length>header.Length){yield return current.ToString();current.Clear();current.Append(header);}
            current.Append(block.Length>maxLength-header.Length?block[..Math.Max(0,maxLength-header.Length-2)]:block);
        }
        if(current.Length>0)yield return current.ToString();
    }
}
