using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MarketWatcher;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException+=OnDispatcherError;
        AppDomain.CurrentDomain.UnhandledException+=(_,args)=>WriteCrash(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException+=(_,args)=>{WriteCrash(args.Exception);args.SetObserved();};
        base.OnStartup(e);
    }
    private void OnDispatcherError(object sender,DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);e.Handled=true;
        var cause=e.Exception;while(cause.InnerException is not null)cause=cause.InnerException;
        System.Windows.MessageBox.Show("Ein Fehler wurde abgefangen und protokolliert:\n"+cause.Message,"MarketWatcher");
    }
    private static void WriteCrash(Exception? ex)
    {
        try{Directory.CreateDirectory(AppPaths.Root);File.AppendAllText(Path.Combine(AppPaths.Root,"errors.log"),$"{DateTime.Now:O}\n{ex}\n\n");}catch{}
    }
}
