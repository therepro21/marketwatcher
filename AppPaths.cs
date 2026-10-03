using System.IO;

namespace MarketWatcher;

public static class AppPaths
{
    public static readonly string Root = Path.Combine(AppContext.BaseDirectory, "data");
    public static readonly string StateFile = Path.Combine(Root, "state.json");
    public static readonly string BackupDirectory = Path.Combine(Root, "backups");
    public static readonly string EdgeProfile = Path.Combine(Root, "EdgeProfile");
    static AppPaths() { Directory.CreateDirectory(Root); Directory.CreateDirectory(EdgeProfile); Directory.CreateDirectory(BackupDirectory); }
}

public static class BrowserFinder
{
    public static string? FindEdge()
    {
        string[] candidates = [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")];
        return candidates.FirstOrDefault(File.Exists);
    }
}

public static class UrlAnalyzer
{
    public static string Platform(Uri uri) => uri.Host.ToLowerInvariant() switch
    {
        var h when h.Contains("willhaben.") => "willhaben",
        var h when h.Contains("kleinanzeigen.") => "Kleinanzeigen",
        var h when h.Contains("vinted.") => "Vinted",
        var h when h.Contains("ebay.") => "eBay",
        var h when h.Contains("markt.de") => "markt.de",
        var h when h.Contains("quoka.de") => "Quoka",
        var h when h.Contains("tutti.ch") => "Tutti",
        _ => uri.Host
    };

    public static string GuessName(Uri uri)
    {
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
            .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1].Replace('+', ' ')), StringComparer.OrdinalIgnoreCase);
        string? term=null;
        foreach (var key in new[] { "query", "q", "keyword", "keywords", "search_text", "text" })
            if (query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)){term=value;break;}
        if(term is null&&uri.Host.Contains("markt.de",StringComparison.OrdinalIgnoreCase))
        {
            var match=System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath,@"/suche/([^/]+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if(match.Success)term=Uri.UnescapeDataString(match.Groups[1].Value.Replace('+',' '));
        }
        if(!string.IsNullOrWhiteSpace(term))
        {
            var details=new List<string>{term};
            if(uri.Host.Contains("vinted.",StringComparison.OrdinalIgnoreCase)&&uri.AbsolutePath.Contains("/5-mens",StringComparison.OrdinalIgnoreCase))details.Add("Herren");
            if(query.TryGetValue("size_ids[]",out var size)&&size=="210")details.Add("XL");
            if(uri.Host.Contains("markt.de",StringComparison.OrdinalIgnoreCase)&&query.TryGetValue("radius",out var radius)&&radius=="250")details.Add("bundesweit");
            return string.Join(" – ",details);
        }
        var part = uri.Segments.LastOrDefault()?.Trim('/').Replace('-', ' ');
        return string.IsNullOrWhiteSpace(part) ? Platform(uri) : Uri.UnescapeDataString(part);
    }
}
