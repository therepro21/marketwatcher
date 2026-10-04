using Microsoft.Playwright;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MarketWatcher;

public sealed class BrowserScanner : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private Process? _edgeProcess;

    public async Task StartAsync(bool headless = true)
    {
        if (_context is not null) return;
        var edge = BrowserFinder.FindEdge() ?? throw new InvalidOperationException("Microsoft Edge wurde nicht gefunden.");
        Directory.CreateDirectory(AppPaths.EdgeProfile);
        var portFile=Path.Combine(AppPaths.EdgeProfile,"DevToolsActivePort");
        try{File.Delete(portFile);}catch(IOException){}
        var start=new ProcessStartInfo(edge){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        start.ArgumentList.Add($"--user-data-dir={Path.GetFullPath(AppPaths.EdgeProfile)}");
        start.ArgumentList.Add("--remote-debugging-port=0");
        start.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        start.ArgumentList.Add("--headless=new");
        start.ArgumentList.Add("--disable-features=WakeLock,MediaSessionService");
        start.ArgumentList.Add("--autoplay-policy=user-gesture-required");
        start.ArgumentList.Add("--mute-audio");
        start.ArgumentList.Add("--window-size=1440,1000");start.ArgumentList.Add("about:blank");
        _edgeProcess=Process.Start(start)??throw new InvalidOperationException("Microsoft Edge konnte nicht gestartet werden.");
        await HideEdgeWindowAsync(_edgeProcess);
        for(var attempt=0;attempt<150&&!File.Exists(portFile);attempt++)await Task.Delay(100);
        if(!File.Exists(portFile)){await StopOwnedEdgeAsync();throw new InvalidOperationException("Edge hat seine lokale Steuerung nicht bereitgestellt.");}
        var port=(await File.ReadAllLinesAsync(portFile)).FirstOrDefault()?.Trim();
        if(string.IsNullOrWhiteSpace(port)){await StopOwnedEdgeAsync();throw new InvalidOperationException("Edge-Steuerport konnte nicht gelesen werden.");}
        _playwright = await Playwright.CreateAsync();
        _browser=await _playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}");
        _context=_browser.Contexts.FirstOrDefault()??throw new InvalidOperationException("Edge-Browserkontext wurde nicht gefunden.");
        await _context.AddInitScriptAsync("""
        (() => {
          const denied = { request: () => Promise.reject(new DOMException('Wake lock disabled by MarketWatcher', 'NotAllowedError')) };
          try { Object.defineProperty(Navigator.prototype, 'wakeLock', { configurable: true, get: () => denied }); } catch {}
          const stopMedia = root => root.querySelectorAll?.('video,audio').forEach(media => { media.muted = true; media.pause(); });
          document.addEventListener('DOMContentLoaded', () => {
            stopMedia(document);
            new MutationObserver(records => records.forEach(record => record.addedNodes.forEach(node => node.nodeType === 1 && stopMedia(node))))
              .observe(document.documentElement, { childList: true, subtree: true });
          }, { once: true });
        })();
        """);
        await HideEdgeWindowAsync(_edgeProcess);
    }

    public async Task<List<Listing>> ScanAsync(SearchJob job)
    {
        await StartAsync();
        var page = _context!.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
        var collected=new Dictionary<string,Listing>(StringComparer.OrdinalIgnoreCase);
        var maxPages=job.Platform is "willhaben" or "Vinted" or "Quoka"?10:1;
        var baseUrl=job.Platform=="willhaben"?SetQueryParameter(job.Url,"rows","90"):job.Url;
        var pageKey=job.Platform=="Quoka"?"pag":"page";
        for(var pageNumber=1;pageNumber<=maxPages;pageNumber++)
        {
            var targetUrl=SetQueryParameter(baseUrl,pageKey,pageNumber.ToString());
            await page.GotoAsync(targetUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            await HideEdgeWindowAsync(_edgeProcess!);
            await page.WaitForTimeoutAsync(1800);
            var title = await page.TitleAsync();
            var body = (await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 10000 })).ToLowerInvariant();
            if(await TryRejectCookiesAsync(page)){await page.WaitForTimeoutAsync(600);body=(await page.Locator("body").InnerTextAsync()).ToLowerInvariant();}
            var challenge=await DetectInterventionAsync(page,title,body);
            if(challenge is not null)throw new BrowserChallengeException(job.Platform,job.Name,targetUrl,challenge);

            // Willhaben adds more organic cards while the page is scrolled. Read
            // only after the document height has remained unchanged repeatedly.
            if(job.Platform is "willhaben" or "Vinted" or "Tutti")
            {
                var stable=0;var previousHeight=0d;
                for(var scroll=0;scroll<30&&stable<3;scroll++)
                {
                    var height=await page.EvaluateAsync<double>("document.documentElement.scrollHeight");
                    await page.EvaluateAsync("window.scrollTo(0, document.documentElement.scrollHeight)");
                    await page.WaitForTimeoutAsync(450);
                    var nextHeight=await page.EvaluateAsync<double>("document.documentElement.scrollHeight");
                    stable=nextHeight<=Math.Max(height,previousHeight)?stable+1:0;previousHeight=nextHeight;
                }
            }

            var raw = await page.Locator("a[href]").EvaluateAllAsync<RawLink[]>("""
        els => els.map(a => {
          const card = a.closest('[data-testid="grid-item"]') || a.closest('.clsy-c-result-list-item,[data-articleid],article,li,[data-testid*="search-result"]') || a.parentElement || a;
          const text = (card.innerText || a.innerText || '').replace(/\s+/g,' ').trim();
          const titleEl = card.querySelector?.('[data-testid$="--description-title"],h2,h3,[data-testid*=title],[class*=title]');
          const title = (titleEl?.innerText || a.innerText || text).replace(/\s+/g,' ').trim();
          const price = (text.match(/(?:€|EUR)\s?\d[\d.,]*|\d[\d.,]*\s?(?:€|EUR)/i)||[''])[0];
          const locationEl = card.querySelector?.('[data-testid*=location],[class*=location],[class*=address],[class*=top--left]');
          const location = (locationEl?.innerText || '').replace(/\s+/g,' ').trim();
          const postal = ((location || text).match(/\b(?:[1-9]\d{3}|\d{5})\b/)||[''])[0];
          const img = card.querySelector?.('img');
          const source = card.querySelector?.('picture source[srcset],source[data-srcset]');
          const candidates = [img?.currentSrc,img?.src,img?.getAttribute('data-src'),img?.getAttribute('data-original'),img?.getAttribute('data-lazy-src'),
            ...(img?.getAttribute('srcset')||'').split(',').map(x=>x.trim().split(/\s+/)[0]),
            ...(img?.getAttribute('data-srcset')||'').split(',').map(x=>x.trim().split(/\s+/)[0]),
            ...(source?.getAttribute('srcset')||source?.getAttribute('data-srcset')||'').split(',').map(x=>x.trim().split(/\s+/)[0])].filter(Boolean);
          const image = candidates.find(x=>/^https?:\/\//i.test(x)) || candidates.find(x=>x.startsWith('//')) || '';
          return { href:a.href, text:title, fullText:text, price, image, postal, location, dataTestId:a.getAttribute('data-testid')||'', externalId:card.getAttribute?.('data-articleid')||'' };
        }).filter(x => x.href && x.text.length > 4)
        """);
            var pageItems=raw.Where(x => IsListingUrl(job.Platform, x.Href) && IsOrganicResult(job.Platform, x.DataTestId) && (job.Platform!="Quoka"||!string.IsNullOrWhiteSpace(x.ExternalId))).Select(x =>
            {
                var clean = x.Href.Split('#','?')[0].TrimEnd('/'); var id = string.IsNullOrWhiteSpace(x.ExternalId)?ExtractId(job.Platform,clean):x.ExternalId;
                var itemTitle = x.Text.Length > 180 ? x.Text[..180] : x.Text;
                return new Listing(id, itemTitle, clean, x.Price, x.Image, x.Postal, x.Location, x.FullText);
            }).Where(x=>MatchesKeyword(job,x.Title,x.SearchText)).GroupBy(x=>x.ExternalId).Select(x=>x.First()).ToList();
            var added=0;foreach(var item in pageItems)if(collected.TryAdd(item.ExternalId,item))added++;
            if(pageNumber>1&&(pageItems.Count==0||added==0))break;
        }
        return collected.Values.Take(500).ToList();
    }

    public async Task SendWhatsAppAsync(WhatsAppSettings settings, SearchJob job, IReadOnlyList<Listing> items)
    {
        if (!settings.Enabled) return;
        var recipients = settings.Recipients.Count>0
            ? settings.Recipients.Where(x=>x.Enabled&&x.Selected).Select(x=>(Name:x.Name,Phone:new string(x.Number.Where(char.IsDigit).ToArray()))).ToList()
            : new List<(string Name,string Phone)>{(settings.RecipientName,new string(settings.RecipientNumber.Where(char.IsDigit).ToArray()))};
        if(recipients.Count==0) throw new InvalidOperationException("WhatsApp ist aktiviert, aber kein Empfänger wurde ausgewählt.");
        if(recipients.Any(x=>x.Phone.Length<8)) throw new InvalidOperationException("Mindestens eine ausgewählte WhatsApp-Nummer fehlt oder ist ungültig.");
        await StartAsync();
        var page = await _context!.NewPageAsync();
        try
        {
            var deliveryErrors=new List<string>();
            foreach(var recipient in recipients)
            {
                try
                {
                    var target = $"https://web.whatsapp.com/send?phone={recipient.Phone}";
                    await page.GotoAsync(target, new PageGotoOptions { WaitUntil=WaitUntilState.DOMContentLoaded, Timeout=45000 });
                    var composer = page.Locator("footer [contenteditable='true']").Last;
                    try { await composer.WaitForAsync(new LocatorWaitForOptions { State=WaitForSelectorState.Visible, Timeout=30000 }); }
                    catch { throw new BrowserChallengeException("WhatsApp Web","Nachrichtenversand / Anmeldung","https://web.whatsapp.com","WhatsApp Web ist nicht angemeldet oder benötigt eine Bestätigung."); }
                    if(settings.IncludeImages)
                    {
                        foreach(var item in items)
                        {
                            var caption=NotificationText.BuildItem(job,item);
                            if(string.IsNullOrWhiteSpace(item.ImageUrl)){await SendWhatsAppTextAsync(page,composer,caption);continue;}
                            await SendWhatsAppImageAsync(page,item.ImageUrl,item.Url,caption);
                        }
                    }
                    else foreach (var message in NotificationText.BuildChunks(job, items, 3000,false))
                    {
                        await SendWhatsAppTextAsync(page,composer,message);
                    }
                }
                catch(Exception ex){deliveryErrors.Add($"{recipient.Name} ({recipient.Phone}): {ex.Message}");}
            }
            if(deliveryErrors.Count>0)throw new InvalidOperationException(string.Join(" | ",deliveryErrors));
        }
        finally { await page.CloseAsync(); }
    }

    private static async Task SendWhatsAppTextAsync(IPage page,ILocator composer,string message)
    {
        var verificationText=VerificationText(message);var before=CountOccurrences(Normalize(await page.Locator("body").InnerTextAsync()),verificationText);
        await composer.FillAsync(message);var send=page.Locator("button[aria-label='Senden'],button[aria-label='Send']").Last;
        await send.WaitForAsync(new(){State=WaitForSelectorState.Visible,Timeout=10000});await send.ClickAsync();
        for(var attempt=0;attempt<20;attempt++){await page.WaitForTimeoutAsync(500);var bodyText=await page.Locator("body").InnerTextAsync();if(string.IsNullOrEmpty((await composer.InnerTextAsync()).Trim())&&CountOccurrences(Normalize(bodyText),verificationText)>before)return;}
        throw new InvalidOperationException("WhatsApp Web hat die Textnachricht nicht bestätigt.");
    }
    private async Task SendWhatsAppImageAsync(IPage page,string imageUrl,string referer,string caption)
    {
        if(imageUrl.StartsWith("//"))imageUrl="https:"+imageUrl;
        var imagePage=await _context!.NewPageAsync();byte[] bytes;string contentType;
        try
        {
            await imagePage.SetExtraHTTPHeadersAsync(new Dictionary<string,string>{{"Referer",referer}});
            var response=await imagePage.GotoAsync(imageUrl,new(){WaitUntil=WaitUntilState.Commit,Timeout=30000});
            if(response is null||!response.Ok)throw new InvalidOperationException($"Anzeigenfoto konnte nicht geladen werden (HTTP {response?.Status}).");
            bytes=await response.BodyAsync();contentType=response.Headers.TryGetValue("content-type",out var type)?type.Split(';')[0]:"image/jpeg";
        }
        finally{await imagePage.CloseAsync();}
        if(bytes.Length==0)throw new InvalidOperationException("Anzeigenfoto war leer.");
        var extension=contentType switch{"image/png"=>".png","image/webp"=>".webp","image/gif"=>".gif",_=>".jpg"};
        var input=page.Locator("input[type='file'][accept*='image']").Last;
        if(await input.CountAsync()==0)
        {
            var attach=page.Locator("button[title='Anhängen'],button[title='Attach'],span[data-icon='plus-rounded']").Last;
            await attach.ClickAsync();input=page.Locator("input[type='file'][accept*='image']").Last;
        }
        await input.WaitForAsync(new(){State=WaitForSelectorState.Attached,Timeout=10000});
        await input.SetInputFilesAsync(new[]{new FilePayload{Name="anzeige"+extension,MimeType=contentType,Buffer=bytes}});
        var preview=page.Locator("div[role='dialog'],div[data-animate-modal-popup='true']").Last;
        await preview.WaitForAsync(new(){State=WaitForSelectorState.Visible,Timeout=15000});
        var captionBox=preview.Locator("[contenteditable='true']").Last;if(await captionBox.CountAsync()>0)await captionBox.FillAsync(caption);
        var send=page.Locator("button[aria-label='Senden'],button[aria-label='Send'],span[data-icon='send'],span[data-icon='wds-ic-send-filled'],[aria-label*='ausgewähltes Element senden'],[aria-label*='selected item' i]").Last;
        await send.WaitForAsync(new(){State=WaitForSelectorState.Visible,Timeout=10000});await send.ClickAsync();
        await preview.WaitForAsync(new(){State=WaitForSelectorState.Hidden,Timeout=20000});
    }

    public Task TestWhatsAppAsync(WhatsAppSettings settings) => SendWhatsAppAsync(
        new WhatsAppSettings { Enabled=true, SendToFirst=settings.SendToFirst, RecipientName=settings.RecipientName, RecipientNumber=settings.RecipientNumber, SendToSecond=settings.SendToSecond, Recipient2Name=settings.Recipient2Name, Recipient2Number=settings.Recipient2Number },
        new SearchJob { Name="Testsuche", Platform="MarketWatcher" },
        [new Listing("test", "Testnachricht erfolgreich", "https://example.com", "")]);
    private static int CountOccurrences(string source,string value)
    {
        if(string.IsNullOrEmpty(value))return 0;var count=0;var index=0;
        while((index=source.IndexOf(value,index,StringComparison.Ordinal))>=0){count++;index+=value.Length;}
        return count;
    }
    private static string Normalize(string value)=>System.Text.RegularExpressions.Regex.Replace(value,"\\s+"," ").Trim();
    private static string VerificationText(string value)
    {
        var normalized=Normalize(value);var first=normalized.TakeWhile(c=>!char.IsLetterOrDigit(c)).Count();
        return first<0?normalized:normalized[first..];
    }

    public static string Fingerprint(string platform, Listing x)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{platform}|{x.ExternalId}|{x.Url}"));
        return Convert.ToHexString(bytes);
    }
    public async Task CloseAsync()
    {
        if(_browser is not null){try{await _browser.CloseAsync();}catch{} _browser=null;}
        _context=null;
        _playwright?.Dispose(); _playwright = null;
        await StopOwnedEdgeAsync();
        CleanDisposableCaches();
    }
    private async Task StopOwnedEdgeAsync()
    {
        var process=_edgeProcess;_edgeProcess=null;if(process is null)return;
        try{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}}
        catch{try{if(!process.HasExited)process.Kill();}catch{}}
        finally{process.Dispose();}
    }
    private static async Task HideEdgeWindowAsync(Process process)
    {
        // Edge may create its taskbar window in a child process. Hide every
        // window belonging to the exact tree started by MarketWatcher.
        for(var attempt=0;attempt<20;attempt++)
        {
            try
            {
                var owned=GetProcessTree(process.Id);
                EnumWindows((handle,_)=>{GetWindowThreadProcessId(handle,out var pid);if(owned.Contains((int)pid))ShowWindowAsync(handle,0);return true;},IntPtr.Zero);
            }
            catch{}
            await Task.Delay(50);
        }
    }
    private static HashSet<int> GetProcessTree(int rootPid)
    {
        var result=new HashSet<int>{rootPid};var entries=new List<(int Pid,int Parent)>();
        var snapshot=CreateToolhelp32Snapshot(2,0);if(snapshot==new IntPtr(-1))return result;
        try
        {
            var entry=new ProcessEntry32{Size=(uint)Marshal.SizeOf<ProcessEntry32>()};
            if(Process32First(snapshot,ref entry))do{entries.Add(((int)entry.ProcessId,(int)entry.ParentProcessId));entry.Size=(uint)Marshal.SizeOf<ProcessEntry32>();}while(Process32Next(snapshot,ref entry));
        }
        finally{CloseHandle(snapshot);}
        var changed=true;while(changed){changed=false;foreach(var item in entries)if(result.Contains(item.Parent)&&result.Add(item.Pid))changed=true;}
        return result;
    }
    private delegate bool EnumWindowsCallback(IntPtr handle,IntPtr parameter);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct ProcessEntry32
    {
        public uint Size,Usage,ProcessId;public IntPtr DefaultHeapId;public uint ModuleId,Threads,ParentProcessId;public int Priority;public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string ExeFile;
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint processId);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool Process32First(IntPtr snapshot,ref ProcessEntry32 entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool Process32Next(IntPtr snapshot,ref ProcessEntry32 entry);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumWindowsCallback callback,IntPtr parameter);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr handle,out uint processId);
    [DllImport("user32.dll")]private static extern bool ShowWindowAsync(IntPtr hWnd,int nCmdShow);
    public static void CleanDisposableCaches()
    {
        var profile=Path.GetFullPath(AppPaths.EdgeProfile);
        var disposable=new[]{
            Path.Combine(profile,"Default","Cache"),Path.Combine(profile,"Default","Code Cache"),Path.Combine(profile,"Default","GPUCache"),
            Path.Combine(profile,"Default","DawnGraphiteCache"),Path.Combine(profile,"Default","DawnWebGPUCache"),Path.Combine(profile,"BrowserMetrics"),
            Path.Combine(profile,"DeferredBrowserMetrics"),Path.Combine(profile,"component_crx_cache"),Path.Combine(profile,"GrShaderCache"),Path.Combine(profile,"ShaderCache")};
        foreach(var path in disposable)try{if(Directory.Exists(path)&&Path.GetFullPath(path).StartsWith(profile+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))Directory.Delete(path,true);}catch{}
    }
    private static async Task<string?> DetectInterventionAsync(IPage page,string title,string body)
    {
        var text=(title+"\n"+body).ToLowerInvariant();
        if(text.Contains("just a moment")||text.Contains("nur einen moment")||text.Contains("cloudflare ray id")||await AnyVisibleAsync(page,new[]{"[id*='cf-chl' i]","iframe[src*='challenge-platform' i]"}))
            return "Cloudflare-Sicherheitsprüfung erkannt.";
        if(text.Contains("verify you are human")||text.Contains("bestätigen sie, dass sie ein mensch")||text.Contains("sicherheitsüberprüfung")||text.Contains("security verification")||await AnyVisibleAsync(page,new[]{"iframe[src*='captcha' i]",".g-recaptcha","[data-sitekey]","[class*='captcha' i]"}))
            return "CAPTCHA bzw. menschliche Bestätigung erkannt.";
        if(await HasVisibleConsentAsync(page))return "Cookie-Einwilligung erkannt; eine eindeutige Option zum Ablehnen aller Cookies war nicht verfügbar.";
        return null;
    }
    private static async Task<bool> TryRejectCookiesAsync(IPage page)
    {
        var selectors=new[]{"#onetrust-reject-all-handler","button:has-text('Alle ablehnen')","button:has-text('Alles ablehnen')","button:has-text('Nur notwendige')","button:has-text('Nur erforderliche')","button:has-text('Reject all')","button:has-text('Reject optional')","button:has-text('Necessary only')"};
        foreach(var selector in selectors)try{var button=page.Locator(selector).First;if(await button.IsVisibleAsync()){await button.ClickAsync();return true;}}catch{}
        return false;
    }
    private static async Task<bool> HasVisibleConsentAsync(IPage page)
    {
        var selectors=new[]{"#onetrust-banner-sdk","[role=dialog] button:has-text('Cookies akzeptieren')","[role=dialog] button:has-text('Alle akzeptieren')","[role=dialog] button:has-text('Accept cookies')","[role=dialog] button:has-text('Accept all')"};
        foreach(var selector in selectors)try{if(await page.Locator(selector).First.IsVisibleAsync())return true;}catch{}
        return false;
    }
    private static async Task<bool> AnyVisibleAsync(IPage page,IEnumerable<string> selectors)
    {
        foreach(var selector in selectors)try{if(await page.Locator(selector).First.IsVisibleAsync())return true;}catch{}
        return false;
    }
    private static bool IsListingUrl(string platform,string url) => platform switch
    {
        "willhaben" => url.Contains("/iad/kaufen-und-verkaufen/d/",StringComparison.OrdinalIgnoreCase),
        "Kleinanzeigen" => url.Contains("/s-anzeige/",StringComparison.OrdinalIgnoreCase),
        "Vinted" => url.Contains("/items/",StringComparison.OrdinalIgnoreCase),
        "eBay" => url.Contains("/itm/",StringComparison.OrdinalIgnoreCase),
        "markt.de" => System.Text.RegularExpressions.Regex.IsMatch(url,@"/a/[0-9a-f]{8}/?",System.Text.RegularExpressions.RegexOptions.IgnoreCase),
        "Quoka" => url.Contains("/anzeige/",StringComparison.OrdinalIgnoreCase),
        "Tutti" => System.Text.RegularExpressions.Regex.IsMatch(url,@"/de/vi/\d+(?:[/?#]|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase),
        _ => true
    };
    private static bool IsOrganicResult(string platform,string dataTestId) =>
        platform!="willhaben" || dataTestId.StartsWith("search-result-entry-header-",StringComparison.OrdinalIgnoreCase);
    private static bool MatchesKeyword(SearchJob job,string title,string searchText)
    {
        if(job.Platform!="willhaben")return true;
        // In this mode Willhaben itself defines the result set. Applying another
        // local text heuristic would incorrectly remove description matches.
        if(job.MatchMode=="title_or_content")return true;
        var match=System.Text.RegularExpressions.Regex.Match(new Uri(job.Url).Query,@"(?:^|[?&])keyword=([^&]+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if(!match.Success)return true;
        var keyword=Uri.UnescapeDataString(match.Groups[1].Value.Replace('+',' '));
        var tokens=keyword.Split(' ',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Where(x=>x.Length>1);
        return tokens.All(x=>title.Contains(x,StringComparison.OrdinalIgnoreCase));
    }
    private static string SetQueryParameter(string url,string key,string value)
    {
        var uri=new Uri(url);var parts=uri.Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries)
            .Where(x=>!x.StartsWith(key+"=",StringComparison.OrdinalIgnoreCase)).ToList();
        parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        var builder=new UriBuilder(uri){Query=string.Join("&",parts)};return builder.Uri.ToString();
    }
    private static string ExtractId(string platform,string url)
    {
        if(platform=="markt.de")return System.Text.RegularExpressions.Regex.Match(url,@"/a/([0-9a-f]{8})(?:/|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value is {Length:>0} marketId?marketId:url;
        return System.Text.RegularExpressions.Regex.Match(url,@"(?:[-/]|=)(\d{6,})(?:[-/?&]|$)").Groups[1].Value is { Length: > 0 } id ? id : url;
    }
    public async ValueTask DisposeAsync()=>await CloseAsync();
    private sealed class RawLink { public string Href { get; set; }=""; public string Text { get; set; }=""; public string FullText { get; set; }=""; public string Price { get; set; }=""; public string Image { get; set; }=""; public string Postal { get; set; }=""; public string Location { get; set; }=""; public string DataTestId { get; set; }=""; public string ExternalId { get; set; }=""; }
}

public sealed class BrowserChallengeException(string platform,string searchName,string url,string message) : Exception(message)
{
    public string Platform { get; }=platform;
    public string SearchName { get; }=searchName;
    public string Url { get; }=url;
}
