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
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;private bool _sidebarCollapsed;
    public ObservableCollection<SearchJob> Searches { get; }=[]; public ObservableCollection<string> Logs { get; }=[];
    public MainWindow()
    {
        InitializeComponent();DataContext=this;
        var logoPath=Path.Combine(AppContext.BaseDirectory,"Assets","marketwatcher.png");
        if(File.Exists(logoPath)){var logo=new System.Windows.Media.Imaging.BitmapImage();logo.BeginInit();logo.CacheOption=System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;logo.UriSource=new Uri(logoPath,UriKind.Absolute);logo.EndInit();logo.Freeze();SidebarLogo.Source=logo;HeaderLogo.Source=logo;}
        CollapseSidebar_Click(this,new RoutedEventArgs());
        _repo=new Repository(AppPaths.StateFile);_repo.Initialize();RefreshJobs();
        _watcher=new WatcherService(_repo,AddLog,RefreshJobs);_watcher.ManualInterventionRequired+=ShowBrowserWarning;AutoStartBox.IsChecked=AutoStartManager.IsEnabled();
        _statusTimer=new(){Interval=TimeSpan.FromSeconds(1)};_statusTimer.Tick+=(_,_)=>UpdateDashboard();_statusTimer.Start();UpdateDashboard();
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
        _statusTimer.Stop();_shutdownComplete=true;_trayIcon.Visible=false;_trayIcon.Dispose();Close();
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
        job.Id=_repo.AddJob(job);_watcher.NotifyScheduleChanged();RefreshJobs();UrlBox.Clear();NameBox.Clear();AddLog($"Suche hinzugefügt: {job.Name}");await _watcher.RunJobAsync(job.Id,true);
    }
    private async void RunNow_Click(object sender,RoutedEventArgs e){if(SearchGrid.SelectedItem is SearchJob j)await _watcher.RunJobAsync(j.Id,false);}
    private async void CardRun_Click(object sender,RoutedEventArgs e){SelectCard(sender);if(SearchGrid.SelectedItem is SearchJob j)await _watcher.RunJobAsync(j.Id,false);}
    private void CardToggle_Click(object sender,RoutedEventArgs e){SelectCard(sender);Toggle_Click(sender,e);}
    private void CardRecipients_Click(object sender,RoutedEventArgs e){SelectCard(sender);AssignRecipients_Click(sender,e);}
    private void CardMore_Click(object sender,RoutedEventArgs e)
    {
        SelectCard(sender);if(SearchGrid.SelectedItem is not SearchJob job||sender is not FrameworkElement button)return;
        var menu=new System.Windows.Controls.ContextMenu{Style=(Style)FindResource("DarkMenu")};
        var open=new System.Windows.Controls.MenuItem{Header="Anzeigeportal / Such-URL öffnen"};open.Click+=(_,_)=>Process.Start(new ProcessStartInfo(job.Url){UseShellExecute=true});menu.Items.Add(open);
        var run=new System.Windows.Controls.MenuItem{Header="Jetzt prüfen"};run.Click+=(_,_)=>RunNow_Click(button,new RoutedEventArgs());menu.Items.Add(run);
        var toggle=new System.Windows.Controls.MenuItem{Header=job.Enabled?"Suche pausieren":"Suche fortsetzen"};toggle.Click+=(_,_)=>Toggle_Click(button,new RoutedEventArgs());menu.Items.Add(toggle);
        var recipients=new System.Windows.Controls.MenuItem{Header="Empfänger zuordnen"};recipients.Click+=(_,_)=>AssignRecipients_Click(button,new RoutedEventArgs());menu.Items.Add(recipients);
        var delete=new System.Windows.Controls.MenuItem{Header="Suche löschen",Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,135,151))};delete.Click+=(_,_)=>Delete_Click(button,new RoutedEventArgs());menu.Items.Add(delete);
        button.ContextMenu=menu;menu.IsOpen=true;
    }
    private void SelectCard(object sender){if(sender is FrameworkElement {DataContext:SearchJob job})SearchGrid.SelectedItem=job;}
    private async void Toggle_Click(object sender,RoutedEventArgs e)
    {
        if(SearchGrid.SelectedItem is not SearchJob job){MessageBox.Show("Bitte zuerst eine Suche auswählen.");return;}
        if(job.Enabled){_repo.SetEnabled(job.Id,false);_watcher.NotifyScheduleChanged();AddLog($"{job.Name}: pausiert.");RefreshJobs();return;}
        var answer=MessageBox.Show("Soll MarketWatcher auch alle während der Pause verpassten Treffer melden?\n\nJa = verpasste Treffer sofort melden\nNein = aktuellen Stand still übernehmen und erst ab jetzt melden\nAbbrechen = pausiert lassen","Suche fortsetzen",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
        if(answer==MessageBoxResult.Cancel)return;
        if(answer==MessageBoxResult.Yes){_repo.SetEnabled(job.Id,true);_watcher.NotifyScheduleChanged();RefreshJobs();await _watcher.RunJobAsync(job.Id,false);}
        else await _watcher.ResumeFromNowAsync(job.Id);
    }
    private void Delete_Click(object sender,RoutedEventArgs e){if(SearchGrid.SelectedItem is not SearchJob j)return;if(MessageBox.Show($"Suche „{j.Name}“ löschen? Die globale Ausschlussdatenbank bleibt erhalten.","Löschen",MessageBoxButton.YesNo)==MessageBoxResult.Yes){_repo.DeleteJob(j.Id);_watcher.NotifyScheduleChanged();RefreshJobs();}}
    private void Email_Click(object sender,RoutedEventArgs e){new EmailSettingsWindow(_repo,_watcher){Owner=this}.ShowDialog();AutoStartBox.IsChecked=AutoStartManager.IsEnabled();}
    private void Overview_Click(object sender,RoutedEventArgs e){NewSearchPanel.Visibility=Visibility.Collapsed;}
    private void Searches_Click(object sender,RoutedEventArgs e){NewSearchPanel.Visibility=Visibility.Visible;UrlBox.Focus();}
    private void ShowNewSearch_Click(object sender,RoutedEventArgs e){NewSearchPanel.Visibility=NewSearchPanel.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;if(NewSearchPanel.Visibility==Visibility.Visible)UrlBox.Focus();}
    private void Recipients_Click(object sender,RoutedEventArgs e){if(SearchGrid.SelectedItem is SearchJob)AssignRecipients_Click(sender,e);else Email_Click(sender,e);}
    private void CollapseSidebar_Click(object sender,RoutedEventArgs e)
    {
        _sidebarCollapsed=!_sidebarCollapsed;SidebarColumn.Width=new GridLength(_sidebarCollapsed?80:250);CollapseButton.Content=_sidebarCollapsed?"›":"‹";
        HeaderLogo.Visibility=_sidebarCollapsed?Visibility.Visible:Visibility.Collapsed;
        SidebarHeader.Margin=_sidebarCollapsed?new Thickness(0,18,0,24):new Thickness(14,18,10,24);
        System.Windows.Controls.Grid.SetColumn(CollapseButton,_sidebarCollapsed?0:1);
        System.Windows.Controls.Grid.SetColumnSpan(CollapseButton,_sidebarCollapsed?2:1);
        CollapseButton.HorizontalAlignment=System.Windows.HorizontalAlignment.Center;
        SidebarStatusContent.HorizontalAlignment=System.Windows.HorizontalAlignment.Center;
        SidebarStatusDot.Margin=_sidebarCollapsed?new Thickness(0):new Thickness(0,0,9,0);
        SidebarStatusBadge.Padding=_sidebarCollapsed?new Thickness(0):new Thickness(11);
        SidebarStatusBadge.Width=_sidebarCollapsed?40:double.NaN;
        SidebarStatusBadge.Height=_sidebarCollapsed?40:double.NaN;
        SidebarStatusBadge.HorizontalAlignment=System.Windows.HorizontalAlignment.Center;
        foreach(var label in new[]{OverviewNavText,SearchesNavText,RecipientsNavText,SettingsNavText})
        {
            var panel=(System.Windows.Controls.StackPanel)label.Parent;var nav=(System.Windows.Controls.Button)panel.Parent;
            nav.Padding=_sidebarCollapsed?new Thickness(10,14,10,14):new Thickness(17,14,17,14);
            nav.HorizontalContentAlignment=_sidebarCollapsed?System.Windows.HorizontalAlignment.Center:System.Windows.HorizontalAlignment.Left;
            var icon=(System.Windows.Controls.TextBlock)panel.Children[0];
            icon.Width=_sidebarCollapsed?28:38;icon.TextAlignment=TextAlignment.Center;
            icon.FontFamily=new System.Windows.Media.FontFamily("Segoe Fluent Icons");
            icon.FontSize=22;icon.Height=28;icon.VerticalAlignment=System.Windows.VerticalAlignment.Center;
            icon.Text=label==OverviewNavText?"\uE80F":label==SearchesNavText?"\uE721":label==RecipientsNavText?"\uE716":"\uE713";
        }
        BrandPanel.Visibility=OverviewNavText.Visibility=SearchesNavText.Visibility=RecipientsNavText.Visibility=SettingsNavText.Visibility=SidebarAgentText.Visibility=SidebarVersionText.Visibility=CopyrightText.Visibility=GithubText.Visibility=_sidebarCollapsed?Visibility.Collapsed:Visibility.Visible;
        CollapseButton.ToolTip=_sidebarCollapsed?"Navigation ausklappen":"Navigation einklappen";
    }
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
        AgentStatus.Text="● Eingabe erforderlich";AgentStatus.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,95,95));
        if(_browserWarning is null)
        {
            RestoreFromTray();_browserWarning=new BrowserWarningWindow(challenge.Platform,challenge.SearchName,challenge.Message,challenge.Url,async url=>await _watcher.OpenProfileAsync(url),async()=>await _watcher.CompleteManualInterventionAsync());
            _browserWarning.Closed+=async(_,_)=>{_browserWarning=null;await _watcher.CompleteManualInterventionAsync();};_browserWarning.Show();_browserWarning.Activate();
        }
        else _browserWarning.UpdateChallenge(challenge.Platform,challenge.SearchName,challenge.Message,challenge.Url);
    });
    private void PlatformLink_Click(object sender,System.Windows.Navigation.RequestNavigateEventArgs e){Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri){UseShellExecute=true});e.Handled=true;}
    private void AddLog(string text)=>Dispatcher.Invoke(()=>
    {
        Logs.Add($"{DateTime.Now:HH:mm:ss}  {text}");
        while(Logs.Count>200)Logs.RemoveAt(0);
        if(Logs.Count>0)LogList.ScrollIntoView(Logs[^1]);
    });
    private void RefreshJobs()=>Dispatcher.Invoke(()=>{Searches.Clear();foreach(var x in _repo.GetJobs())Searches.Add(x);UpdateDashboard();});
    private void UpdateDashboard()
    {
        if(!IsInitialized)return;var jobs=_repo.GetJobs();var active=jobs.Where(x=>x.Enabled).ToList();ActiveSearchText.Text=$"{active.Count} von {jobs.Count}";
        BrowserModeText.Text=_repo.GetGeneral().KeepBrowserOpen?active.Count>0?"Edge bleibt unsichtbar geöffnet":"Edge geschlossen · keine aktive Suche":"Edge wird nach jedem Lauf geschlossen";
        var last=jobs.Where(x=>x.LastRunUtc.HasValue).OrderByDescending(x=>x.LastRunUtc).FirstOrDefault();SystemLastRunText.Text=last is null?"Noch keine Prüfung":$"{last.LastRunLabel} · {last.Platform}";
        SystemOffersText.Text=$"{jobs.Sum(x=>x.SeenCount)} gespeicherte Treffer";var statistics=_repo.GetStatistics();SystemNotificationsText.Text=$"{statistics.SuccessfulNotifications} Treffer";SystemErrorsText.Text=statistics.Errors.ToString();SystemErrorsText.Foreground=statistics.Errors==0?new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102,233,157)):new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,135,151));
        if(active.Count==0){NextCheckText.Text="Keine aktive Suche";return;}
        var wait=active.Select(x=>x.LastRunUtc is null?TimeSpan.Zero:TimeSpan.FromSeconds(x.IntervalSeconds)-(DateTime.UtcNow-x.LastRunUtc.Value)).Min();if(wait<TimeSpan.Zero)wait=TimeSpan.Zero;
        NextCheckText.Text=wait.TotalSeconds<1?"jetzt":wait.TotalMinutes>=1?$"in {(int)wait.TotalMinutes} Min. {wait.Seconds} Sek.":$"in {Math.Max(1,(int)Math.Ceiling(wait.TotalSeconds))} Sek.";
    }
}
