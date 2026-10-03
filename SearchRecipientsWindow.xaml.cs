using System.Windows;

namespace MarketWatcher;

public partial class SearchRecipientsWindow:Window
{
    private readonly Repository _repo;private readonly SearchJob _job;private readonly List<WhatsAppRecipient> _wa;private readonly List<TelegramRecipient> _tg;
    public SearchRecipientsWindow(Repository repo,SearchJob job)
    {
        InitializeComponent();_repo=repo;_job=job;SearchName.Text=job.Name;
        var waSelected=job.RecipientIds.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToHashSet();
        _wa=repo.GetWhatsApp().Recipients.Where(x=>x.Enabled).ToList();foreach(var x in _wa)x.Selected=waSelected.Contains(x.Id);WhatsAppList.ItemsSource=_wa;
        var tgSelected=job.TelegramRecipientIds.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToHashSet();
        _tg=repo.GetTelegram().Recipients.Where(x=>x.Enabled).ToList();foreach(var x in _tg)x.Selected=tgSelected.Contains(x.Id);TelegramList.ItemsSource=_tg;
    }
    private void Save_Click(object sender,RoutedEventArgs e){_repo.UpdateRecipients(_job.Id,string.Join(',',_wa.Where(x=>x.Selected).Select(x=>x.Id)),string.Join(',',_tg.Where(x=>x.Selected).Select(x=>x.Id)));DialogResult=true;}
    private void Cancel_Click(object sender,RoutedEventArgs e)=>DialogResult=false;
}
