using System.Globalization;
using System.Text.RegularExpressions;

namespace MarketWatcher;

public static class AmazonRules
{
    public static string Asin(string url)
    {
        var match=Regex.Match(new Uri(url).AbsolutePath,@"/(?:dp|gp/product|gp/offer-listing)/([A-Z0-9]{10})(?:/|$)",RegexOptions.IgnoreCase);
        return match.Success?match.Groups[1].Value.ToUpperInvariant():throw new InvalidOperationException("Amazon-Produktlink mit ASIN benötigt (z. B. /dp/B08KG61KW2).");
    }
    public static decimal? Price(string value)
    {
        var match=Regex.Match(value.Replace("\u00a0"," "),@"(\d[\d.]*,\d{2})\s*(?:€|EUR)|(?:€|EUR)\s*(\d[\d.]*,\d{2})");
        var number=match.Success?(match.Groups[1].Success?match.Groups[1].Value:match.Groups[2].Value):"";
        return decimal.TryParse(number,NumberStyles.Number,CultureInfo.GetCultureInfo("de-DE"),out var p)?p:null;
    }
    public static (Dictionary<string,bool> Conditions,List<string> Alerts,string Summary) Evaluate(SearchJob job,AmazonObservation observation)
    {
        var s=job.Amazon;
        var newOffers=observation.Offers.Where(x=>!x.Used).ToList();
        var used=observation.Offers.Where(x=>x.Used&&(!s.OnlyAmazonResale||x.Resale)).ToList();
        var newPrice=newOffers.Where(x=>x.Price.HasValue).Select(x=>x.Price!.Value).Cast<decimal?>().Min();
        var usedPrice=used.Where(x=>x.Price.HasValue).Select(x=>x.Price!.Value).Cast<decimal?>().Min();
        var conditions=new Dictionary<string,bool>{
            ["available"]=s.NotifyAvailable&&observation.Offers.Count>0,
            ["new"]=s.WatchNewPrice&&s.NewBelow.HasValue&&newPrice.HasValue&&newPrice.Value<=s.NewBelow.Value,
            ["used"]=s.WatchUsed&&used.Count>0&&(!s.UsedBelow.HasValue||(usedPrice.HasValue&&usedPrice.Value<=s.UsedBelow.Value)),
            ["prime"]=s.NotifyPrime&&observation.Offers.Any(x=>x.Prime)
        };
        string Money(decimal? p)=>p.HasValue?p.Value.ToString("0.00",CultureInfo.GetCultureInfo("de-DE"))+" €":"Preis nicht angegeben";
        var reasons=new Dictionary<string,string>{
            ["available"]="Wieder lieferbar",
            ["new"]="Neu zum Zielpreis oder günstiger: "+Money(newPrice),
            ["used"]=(s.OnlyAmazonResale?"Amazon Retourenkauf":"Gebraucht")+" verfügbar: "+Money(usedPrice),
            ["prime"]="Angebot mit Prime-Versand verfügbar"
        };
        var alerts=conditions.Where(x=>x.Value&&(!job.AmazonConditions.TryGetValue(x.Key,out var before)||!before)).Select(x=>reasons[x.Key]).ToList();
        var summary=$"Neu: {(newOffers.Count>0?Money(newPrice):"nicht lieferbar")} · Gebraucht: {(used.Count>0?Money(usedPrice):"nicht lieferbar")} · Prime: {(observation.Offers.Any(x=>x.Prime)?"ja":"nicht erkannt")}";
        return (conditions,alerts,summary);
    }
}
