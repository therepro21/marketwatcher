using System.Windows;
using System.Windows.Threading;
namespace MarketWatcher;
public partial class BrowserWarningWindow : Window
{
    private string _url;private readonly Func<string,Task> _open;private readonly Func<Task> _done;
    public BrowserWarningWindow(string platform,string searchName,string detail,string url,Func<string,Task> open,Func<Task> done)
    {
        InitializeComponent();_open=open;_done=done;_url=url;UpdateChallenge(platform,searchName,detail,url);
        var releaseTopmost=new DispatcherTimer{Interval=TimeSpan.FromSeconds(8)};
        releaseTopmost.Tick+=(_,_)=>{Topmost=false;releaseTopmost.Stop();};releaseTopmost.Start();
    }
    public void UpdateChallenge(string platform,string searchName,string detail,string url){PlatformText.Text=platform;SearchText.Text=searchName;DetailText.Text=detail;ChallengeText.Text=detail.StartsWith("Cloudflare",StringComparison.OrdinalIgnoreCase)?"⚠ CLOUDFLARE-PRÜFUNG":detail.StartsWith("Cookie",StringComparison.OrdinalIgnoreCase)?"⚠ COOKIE-ABFRAGE":detail.StartsWith("CAPTCHA",StringComparison.OrdinalIgnoreCase)?"⚠ CAPTCHA-PRÜFUNG":"⚠ EINGABE ERFORDERLICH";_url=url;Title=$"MarketWatcher – {platform}: {searchName}";}
    private void Later_Click(object sender,RoutedEventArgs e)=>Close();
    private async void Open_Click(object sender,RoutedEventArgs e){try{IsEnabled=false;await _open(_url);OpenButton.Visibility=Visibility.Collapsed;DoneButton.Visibility=Visibility.Visible;IsEnabled=true;}catch(Exception ex){IsEnabled=true;System.Windows.MessageBox.Show(ex.Message,"Edge konnte nicht geöffnet werden",MessageBoxButton.OK,MessageBoxImage.Error);}}
    private async void Done_Click(object sender,RoutedEventArgs e){try{IsEnabled=false;await _done();Close();}catch(Exception ex){IsEnabled=true;System.Windows.MessageBox.Show(ex.Message,"Edge konnte nicht geschlossen werden",MessageBoxButton.OK,MessageBoxImage.Error);}}
}
