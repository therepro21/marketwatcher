using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Forms=System.Windows.Forms;
using Drawing=System.Drawing;
using MessageBox=System.Windows.MessageBox;

namespace MarketWatcher;
public partial class MainWindow : Window
{
    private readonly Repository _repo; private readonly WatcherService _watcher; private readonly Forms.NotifyIcon _trayIcon; private bool _shutdownComplete; private bool _exitRequested;private BrowserWarningWindow? _browserWarning;
    public ObservableCollection<SearchJob> Searches { get; }=[]; public ObservableCollection<string> Logs { get; }=[];
    public MainWindow()
    {
        InitializeComponent();DataContext=this;_repo=new Repository(AppPaths.StateFile);_repo.Initialize();RefreshJobs();
        _watcher=new WatcherService(_repo,AddLog,RefreshJobs);_watcher.ManualInterventionRequired+=ShowBrowserWarning;AutoStartBox.IsChecked=AutoStartManager.IsEnabled();
        var iconPath=Path.Combine(AppContext.BaseDirectory,"Assets","marketwatcher.ico");
        _trayIcon=new Forms.NotifyIcon{Icon=File.Exists(iconPath)?new Drawing.Icon(iconPath):Drawing.SystemIcons.Application,Text="MarketWatcher läuft – Suchagent aktiv",Visible=true};
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("MarketWatcher öffnen",null,(_,_)=>Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Beenden",null,(_,_)=>Dispatcher.Invoke(()=>{_exitRequested=true;Close();}));
        _trayIcon.ContextMenuStrip=menu;_trayIcon.DoubleClick+=(_,_)=>Dispatcher.Invoke(RestoreFromTray);
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized)Hide();};
        Loaded+=async(_,_)=>
        {
            try
            {
                await _watcher.StartAsync();AgentStatus.Text="● Agent aktiv";
                var automaticStart=Environment.GetCommandLineArgs().Any(x=>x.Equals("--autostart",StringComparison.OrdinalIgnoreCase));
                if(_repo.RequiresReauthentication)MessageBox.Show("MarketWatcher wurde unter einem anderen Windows-Benutzer oder auf einem anderen Computer gestartet. Suchen und Ausschlussliste wurden übernommen. Bitte E-Mail-/Telegram-Zugangsdaten neu eingeben und WhatsApp Web per QR-Code neu anmelden.","Neue Windows-Umgebung erkannt");
                if(automaticStart){WindowState=WindowState.Minimized;Hide();}
                else{Show();WindowState=WindowState.Normal;Activate();}
            }
            catch(Exception ex){AgentStatus.Text="● Browserfehler";AddLog(ex.Message);}
        };
    }
    protected override async void OnClosing(CancelEventArgs e)
    {
        if(!_exitRequested&&!_shutdownComplete){e.Cancel=true;Hide();return;}
        if(_shutdownComplete){base.OnClosing(e);return;}
        e.Cancel=true;AgentStatus.Text="● Agent wird beendet";
        try{await _watcher.DisposeAsync();}catch(Exception ex){AddLog("Fehler beim Beenden: "+ex.Message);}
        _shutdownComplete=true;_trayIcon.Visible=false;_trayIcon.Dispose();Close();
    }
    private void RestoreFromTray(){Show();WindowState=WindowState.Normal;Activate();}
    private async void AddSearch_Click(object sender,RoutedEventArgs e)
    {
        if(!Uri.TryCreate(UrlBox.Text.Trim(),UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")){MessageBox.Show("Bitte eine gültige Such-URL einfügen.");return;}
        if(!int.TryParse(IntervalBox.Text,out var seconds)||seconds<30)seconds=60;
        var matchMode=(MatchModeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "title_or_content";
        var recipientIds=string.Join(',',_repo.GetWhatsApp().Recipients.Where(x=>x.Enabled).Select(x=>x.Id));
        var telegramIds=string.Join(',',_repo.GetTelegram().Recipients.Where(x=>x.Enabled).Select(x=>x.Id));
        var job=new SearchJob{Name=string.IsNullOrWhiteSpace(NameBox.Text)?UrlAnalyzer.GuessName(uri):NameBox.Text.Trim(),Url=uri.ToString(),Platform=UrlAnalyzer.Platform(uri),IntervalSeconds=seconds,Enabled=true,Status="Neu – erster Lauf wird Basisbestand",MatchMode=matchMode,RecipientIds=recipientIds,TelegramRecipientIds=telegramIds};
        job.Id=_repo.AddJob(job);RefreshJobs();UrlBox.Clear();NameBox.Clear();AddLog($"Suche hinzugefügt: {job.Name}");await _watcher.RunJobAsync(job.Id,true);
    }
    private async void RunNow_Click(object sender,RoutedEventArgs e){if(SearchGrid.SelectedItem is SearchJob j)await _watcher.RunJobAsync(j.Id,false);}
    private async void Toggle_Click(object sender,RoutedEventArgs e)
    {
        if(SearchGrid.SelectedItem is not SearchJob job){MessageBox.Show("Bitte zuerst eine Suche auswählen.");return;}
        if(job.Enabled){_repo.SetEnabled(job.Id,false);AddLog($"{job.Name}: pausiert.");RefreshJobs();return;}
        var answer=MessageBox.Show("Soll MarketWatcher auch alle während der Pause verpassten Treffer melden?\n\nJa = verpasste Treffer sofort melden\nNein = aktuellen Stand still übernehmen und erst ab jetzt melden\nAbbrechen = pausiert lassen","Suche fortsetzen",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
        if(answer==MessageBoxResult.Cancel)return;
        if(answer==MessageBoxResult.Yes){_repo.SetEnabled(job.Id,true);RefreshJobs();await _watcher.RunJobAsync(job.Id,false);}
        else await _watcher.ResumeFromNowAsync(job.Id);
    }
    private void Delete_Click(object sender,RoutedEventArgs e){if(SearchGrid.SelectedItem is not SearchJob j)return;if(MessageBox.Show($"Suche „{j.Name}“ löschen? Die globale Ausschlussdatenbank bleibt erhalten.","Löschen",MessageBoxButton.YesNo)==MessageBoxResult.Yes){_repo.DeleteJob(j.Id);RefreshJobs();}}
    private void Email_Click(object sender,RoutedEventArgs e){new EmailSettingsWindow(_repo,_watcher){Owner=this}.ShowDialog();AutoStartBox.IsChecked=AutoStartManager.IsEnabled();}
    private void AssignRecipients_Click(object sender,RoutedEventArgs e)
    {
        if(SearchGrid.SelectedItem is not SearchJob job){MessageBox.Show("Bitte zuerst eine Suche auswählen.");return;}
        if(new SearchRecipientsWindow(_repo,job){Owner=this}.ShowDialog()==true)RefreshJobs();
    }
    private void AutoStart_Changed(object sender,RoutedEventArgs e)
    {
        if(!IsLoaded)return;
        try{AutoStartManager.SetEnabled(AutoStartBox.IsChecked==true);AddLog(AutoStartBox.IsChecked==true?"Windows-Autostart aktiviert.":"Windows-Autostart deaktiviert.");}
        catch(Exception ex){MessageBox.Show("Autostart konnte nicht geändert werden: "+ex.Message);AutoStartBox.IsChecked=AutoStartManager.IsEnabled();}
    }
    private async void OpenProfile_Click(object sender,RoutedEventArgs e){try{await _watcher.OpenProfileAsync();}catch(Exception ex){MessageBox.Show(ex.Message);}}
    private void ShowBrowserWarning(BrowserChallengeException challenge)=>Dispatcher.BeginInvoke(() =>
    {
        RestoreFromTray();AgentStatus.Text="● Eingabe erforderlich";AgentStatus.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,95,95));
        if(_browserWarning is null){_browserWarning=new BrowserWarningWindow(challenge.SearchName,challenge.Message,challenge.Url,async url=>await _watcher.OpenProfileAsync(url));_browserWarning.Closed+=(_,_)=>_browserWarning=null;_browserWarning.Show();}
        else _browserWarning.UpdateChallenge(challenge.SearchName,challenge.Message,challenge.Url);
        _browserWarning.Activate();
    });
    private void PlatformLink_Click(object sender,System.Windows.Navigation.RequestNavigateEventArgs e){Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri){UseShellExecute=true});e.Handled=true;}
    private void AddLog(string text)=>Dispatcher.Invoke(()=>{Logs.Insert(0,$"{DateTime.Now:HH:mm:ss}  {text}");while(Logs.Count>200)Logs.RemoveAt(Logs.Count-1);});
    private void RefreshJobs()=>Dispatcher.Invoke(()=>{Searches.Clear();foreach(var x in _repo.GetJobs())Searches.Add(x);});
}
