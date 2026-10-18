using Microsoft.Playwright;

namespace MarketWatcher;

public sealed partial class BrowserScanner
{
    public async Task<AmazonObservation> ScanAmazonAsync(SearchJob job)
    {
        for(var attempt=1;;attempt++)
        {
            try{return await ScanAmazonCoreAsync(job);}
            catch(Exception ex) when(attempt<3&&!Suspended&&(ex is PlaywrightException||ex is InvalidOperationException)&&ex is not BrowserChallengeException)
            {
                Diagnostic?.Invoke($"Amazon · Ladefehler · Versuch {attempt}/3 fehlgeschlagen: {ex.Message.Split('\n')[0]} · Seite wird erneut geöffnet.");
                if(!IsRunning||ex.Message.Contains("has been closed",StringComparison.OrdinalIgnoreCase))await CloseAsync();
                await Task.Delay(attempt*1500);
            }
        }
    }
    private async Task<AmazonObservation> ScanAmazonCoreAsync(SearchJob job)
    {
        var asin=AmazonRules.Asin(job.Url);await StartAsync();
        var page=await _context!.NewPageAsync();
        try
        {
            await page.GotoAsync(job.Url,new(){WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=45000});
            await page.WaitForTimeoutAsync(2000);
            await TryRejectCookiesAsync(page);
            var body=(await page.Locator("body").InnerTextAsync()).ToLowerInvariant();
            var challenge=await DetectInterventionAsync(page,await page.TitleAsync(),body);
            if(challenge is not null||body.Contains("geben sie die zeichen")||body.Contains("enter the characters"))
                throw new BrowserChallengeException("Amazon",job.Name,job.Url,challenge??"CAPTCHA-Prüfung bei Amazon.");
            var title=(await page.Locator("span#productTitle").First.InnerTextAsync(new(){Timeout=15000})).Trim();
            var imageLocator=page.Locator("#landingImage,#imgBlkFront").First;
            var image=await imageLocator.CountAsync()>0?await imageLocator.GetAttributeAsync("src"):null;
            var observation=new AmazonObservation{Title=title,Url=job.Url,ImageUrl=image??""};
            var main=await page.EvaluateAsync<AmazonRawOffer?>("""
            () => {
              const root=document.querySelector('#desktop_buybox,#buybox');
              if(!root||!root.querySelector('#add-to-cart-button,#buy-now-button,input[name="submit.add-to-cart"]'))return null;
              const text=root.innerText||'';
              const priceNode=root.querySelector('.a-price:not(.a-text-price)');
              const whole=priceNode?.querySelector('.a-price-whole')?.textContent?.replace(/[^\d.]/g,'');
              const fraction=priceNode?.querySelector('.a-price-fraction')?.textContent?.trim();
              const price=whole&&fraction?whole+','+fraction+' €':priceNode?.querySelector('.a-offscreen')?.textContent?.trim()||'';
              const prime=!!root.querySelector('[aria-label*="Prime"],img[alt*="Prime"],.a-icon-prime');
              return {text,price,prime};
            }
            """);
            if(main is not null)observation.Offers.Add(ToAmazonOffer(main));
            // Read the offer list even when the primary buy box has no stock.
            var ingress=page.Locator("#aod-ingress-link,#buybox-see-all-buying-choices a,a[href*='/gp/offer-listing/']").First;
            if(await ingress.CountAsync()>0)
            {
                await ingress.ClickAsync();
                await page.Locator("#aod-container,#aod-offer-list,#olpOfferList").First.WaitForAsync(new(){State=WaitForSelectorState.Visible,Timeout=20000});
                await page.WaitForTimeoutAsync(1000);
                var offers=await page.EvaluateAsync<AmazonRawOffer[]>("""
                () => [...document.querySelectorAll('#aod-pinned-offer,#aod-offer,.olpOffer')].map(root=>{
                  const priceNode=root.querySelector('.a-price:not(.a-text-price)');
                  const whole=priceNode?.querySelector('.a-price-whole')?.textContent?.replace(/[^\d.]/g,'');
                  const fraction=priceNode?.querySelector('.a-price-fraction')?.textContent?.trim();
                  const price=whole&&fraction?whole+','+fraction+' €':root.querySelector('.a-price:not(.a-text-price) .a-offscreen,.olpOfferPrice')?.textContent?.trim()||'';
                  const text=root.innerText||'';
                  const purchasable=!!root.querySelector('input[name*="addToCart"],input[name*="add-to-cart"],[name="submit.addToCart"],.a-button-input,[data-action="aod-atc-action"]');
                  const prime=!!root.querySelector('[aria-label*="Prime"],img[alt*="Prime"],.a-icon-prime');
                  return {text,price,prime,purchasable};
                }).filter(x=>x.purchasable)
                """);
                if(offers.Length>0)
                {
                    observation.Offers.Clear();
                    observation.Offers.AddRange(offers.Select(ToAmazonOffer));
                }
                if(offers.Length==0)
                {
                    var text=(await page.Locator("body").InnerTextAsync()).ToLowerInvariant();
                    if(!text.Contains("keine angebote")&&!text.Contains("nicht verfügbar")&&!text.Contains("no offers"))
                        throw new InvalidOperationException("Amazon-Angebotsliste konnte nicht sicher ausgewertet werden; bisheriger Zustand bleibt erhalten.");
                }
            }
            else if(main is null&&!body.Contains("derzeit nicht verfügbar")&&!body.Contains("nicht auf lager")&&!body.Contains("currently unavailable"))
                throw new InvalidOperationException("Amazon-Lieferstatus nicht eindeutig erkannt; bisheriger Zustand bleibt erhalten.");
            if(observation.Offers.Count>0&&observation.Offers.All(x=>!x.Price.HasValue))
                throw new InvalidOperationException("Amazon-Angebote geladen, Preise fehlen aber noch; Seite wird erneut gelesen.");
            return observation;
        }
        finally{await page.CloseAsync();}
    }
    private static AmazonOffer ToAmazonOffer(AmazonRawOffer raw)
    {
        var text=raw.Text.ToLowerInvariant();
        return new AmazonOffer{Price=AmazonRules.Price(raw.Price),Used=text.Contains("gebraucht")||text.Contains("used")||text.Contains("retourenkauf")||text.Contains("warehouse"),Prime=raw.Prime,
            Resale=text.Contains("amazon retourenkauf")||text.Contains("amazon resale")||text.Contains("amazon warehouse")};
    }
    private sealed class AmazonRawOffer
    {
        public string Text {get;set;}="";
        public string Price {get;set;}="";
        public bool Prime {get;set;}
        public bool Purchasable {get;set;}
    }
}
