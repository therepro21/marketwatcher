using System.IO;

namespace MarketWatcher;

public static class AppPaths
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarktWaechter");
    public static readonly string Database = Path.Combine(Root, "marktwaechter.db");
    public static readonly string BackupDirectory = Path.Combine(Root, "Backups");
    public static readonly string DatabaseBackup = Path.Combine(BackupDirectory, "marktwaechter-latest.db");
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
        _ => uri.Host
    };

    public static string GuessName(Uri uri)
    {
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
            .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1].Replace('+', ' ')), StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "query", "q", "keyword", "keywords", "search_text", "text" })
            if (query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        var part = uri.Segments.LastOrDefault()?.Trim('/').Replace('-', ' ');
        return string.IsNullOrWhiteSpace(part) ? Platform(uri) : Uri.UnescapeDataString(part);
    }
}
