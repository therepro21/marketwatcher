using System.Windows;
namespace MarketWatcher;
public partial class BrowserWarningWindow : Window
{
    private string _url;private readonly Func<string,Task> _open;
    public BrowserWarningWindow(string platform,string searchName,string detail,string url,Func<string,Task> open){InitializeComponent();_open=open;_url=url;UpdateChallenge(platform,searchName,detail,url);}
    public void UpdateChallenge(string platform,string searchName,string detail,string url){PlatformText.Text=platform;SearchText.Text=searchName;DetailText.Text=detail;_url=url;Title=$"MarketWatcher – {platform}: {searchName}";}
    private void Later_Click(object sender,RoutedEventArgs e)=>Close();
    private async void Open_Click(object sender,RoutedEventArgs e){try{IsEnabled=false;await _open(_url);Close();}catch(Exception ex){IsEnabled=true;System.Windows.MessageBox.Show(ex.Message,"Edge konnte nicht geöffnet werden",MessageBoxButton.OK,MessageBoxImage.Error);}}
}
