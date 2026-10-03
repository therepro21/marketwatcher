using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace MarketWatcher;

public static class SecretStore
{
    public static string Protect(string value) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string value) => string.IsNullOrWhiteSpace(value) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
}

public static class EmailSender
{
    public static async Task SendAsync(EmailSettings settings, SearchJob job, IReadOnlyList<Listing> items)
    {
        if (string.IsNullOrWhiteSpace(settings.User) || string.IsNullOrWhiteSpace(settings.Recipient) || string.IsNullOrWhiteSpace(settings.ProtectedPassword)) return;
        var html = new StringBuilder($"<h2>{items.Count} neue Treffer: {WebUtility.HtmlEncode(job.Name)}</h2>");
        foreach(var x in items)
        {
            var details=new[]{x.Price,x.PostalCode,x.Location,job.Platform}.Where(v=>!string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase);
            html.Append("<div style=\"margin:0 0 18px\">");
            if(settings.IncludeImages&&!string.IsNullOrWhiteSpace(x.ImageUrl))html.Append($"<a href=\"{WebUtility.HtmlEncode(x.Url)}\"><img src=\"{WebUtility.HtmlEncode(x.ImageUrl)}\" alt=\"\" style=\"width:120px;height:90px;object-fit:cover;border-radius:8px;float:left;margin:0 12px 8px 0\"></a>");
            html.Append($"<b>{WebUtility.HtmlEncode(x.Title)}</b><br>{WebUtility.HtmlEncode(string.Join(" · ",details))}<br><a href=\"{WebUtility.HtmlEncode(x.Url)}\">Anzeige öffnen</a><div style=\"clear:both\"></div></div>");
        }
        using var message = new MailMessage(settings.User, settings.Recipient, $"MarktWächter: {items.Count} neue Treffer für {job.Name}", html.ToString()) { IsBodyHtml=true };
        using var smtp = new SmtpClient(settings.Host,settings.Port){EnableSsl=true,Credentials=new NetworkCredential(settings.User,SecretStore.Unprotect(settings.ProtectedPassword))};
        await smtp.SendMailAsync(message);
    }
}
