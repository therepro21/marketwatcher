using Microsoft.Win32;

namespace MarketWatcher;

public static class AutoStartManager
{
    private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName="MarketWatcher";

    public static bool IsEnabled()
    {
        using var key=Registry.CurrentUser.OpenSubKey(RunKey,false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(RunKey,true);
        if(!enabled){key.DeleteValue(ValueName,false);return;}
        var executable=Environment.ProcessPath ?? throw new InvalidOperationException("Der Programmpfad konnte nicht ermittelt werden.");
        key.SetValue(ValueName,$"\"{executable}\" --autostart",RegistryValueKind.String);
    }
}
