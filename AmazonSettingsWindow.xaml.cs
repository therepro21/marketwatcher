using System.Globalization;
using System.Windows;

namespace MarketWatcher;
public partial class AmazonSettingsWindow:Window
{
    public AmazonWatchSettings Settings {get;private set;}=new();
    public int IntervalSeconds {get;private set;}=900;
    public AmazonSettingsWindow(string url,AmazonWatchSettings? settings=null,int seconds=900)
    {
        InitializeComponent();var asin=AmazonRules.Asin(url);ProductText.Text="Produkt "+asin+" · "+new Uri(url).Host;
        settings??=asin=="B08KG61KW2"?new(){NewBelow=20,UsedBelow=18}:asin=="B0F93FPY3H"?new(){NewBelow=25}:new();
        AvailableBox.IsChecked=settings.NotifyAvailable;NewBox.IsChecked=settings.WatchNewPrice;UsedBox.IsChecked=settings.WatchUsed;ResaleBox.IsChecked=settings.OnlyAmazonResale;PrimeBox.IsChecked=settings.NotifyPrime;
        NewPriceBox.Text=settings.NewBelow?.ToString(CultureInfo.GetCultureInfo("de-DE"))??"";UsedPriceBox.Text=settings.UsedBelow?.ToString(CultureInfo.GetCultureInfo("de-DE"))??"";MinutesBox.Text=(seconds/60m).ToString(CultureInfo.GetCultureInfo("de-DE"));
    }
    private void Cancel_Click(object sender,RoutedEventArgs e)=>DialogResult=false;
    private void Save_Click(object sender,RoutedEventArgs e)
    {
        decimal? Parse(string value)=>string.IsNullOrWhiteSpace(value)?null:decimal.Parse(value.Replace('.',','),NumberStyles.AllowDecimalPoint,CultureInfo.GetCultureInfo("de-DE"));
        try
        {
            var newPrice=Parse(NewPriceBox.Text);var usedPrice=Parse(UsedPriceBox.Text);var minutes=Parse(MinutesBox.Text);
            if(minutes is null||minutes<1||minutes>1440)throw new FormatException("Intervall: 1 bis 1440 Minuten.");
            if(newPrice<=0||usedPrice<=0)throw new FormatException("Preisgrenzen müssen größer als null sein.");
            if(NewBox.IsChecked==true&&!newPrice.HasValue)throw new FormatException("Bitte eine Preisgrenze für Neuware eingeben.");
            if(NewBox.IsChecked!=true&&UsedBox.IsChecked!=true&&AvailableBox.IsChecked!=true&&PrimeBox.IsChecked!=true)throw new FormatException("Mindestens einen Alarm auswählen.");
            Settings=new(){NotifyAvailable=AvailableBox.IsChecked==true,WatchNewPrice=NewBox.IsChecked==true,NewBelow=newPrice,WatchUsed=UsedBox.IsChecked==true,UsedBelow=usedPrice,NotifyPrime=PrimeBox.IsChecked==true,OnlyAmazonResale=ResaleBox.IsChecked==true};
            IntervalSeconds=(int)(minutes.Value*60);DialogResult=true;
        }
        catch(Exception ex) when(ex is FormatException or OverflowException){System.Windows.MessageBox.Show(this,ex.Message,"Amazon-Regeln prüfen");}
    }
}
