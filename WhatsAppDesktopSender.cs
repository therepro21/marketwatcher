using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace MarketWatcher;

public static class WhatsAppDesktopSender
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    private const byte EnterKey = 0x0D;
    private const uint KeyUp = 0x0002;

    public static async Task SendAsync(string recipientName, string phone, string message, CancellationToken cancellationToken = default)
    {
        phone = new string(phone.Where(char.IsDigit).ToArray());
        if (phone.Length < 8) throw new InvalidOperationException("Die WhatsApp-Nummer ist ungültig.");
        if (string.IsNullOrWhiteSpace(recipientName)) throw new InvalidOperationException("Für den sicheren Desktop-Versand muss ein Kontaktname eingetragen sein.");

        var uri = $"whatsapp://send?phone={phone}&text={Uri.EscapeDataString(message)}";
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

        AutomationElement? window = null;
        for (var attempt = 0; attempt < 30 && window is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, cancellationToken);
            window = FindWhatsAppWindow();
        }
        if (window is null) throw new InvalidOperationException("WhatsApp Beta wurde nicht als bedienbares Fenster gefunden.");

        SetForegroundWindow(new IntPtr(window.Current.NativeWindowHandle));
        if (IsWebViewContentHidden(window))
        {
            await Task.Delay(3000, cancellationToken);
            SetForegroundWindow(new IntPtr(window.Current.NativeWindowHandle));
            keybd_event(EnterKey, 0, 0, UIntPtr.Zero);
            keybd_event(EnterKey, 0, KeyUp, UIntPtr.Zero);
            return;
        }
        AutomationElement? contact = null;
        AutomationElement? send = null;
        for (var attempt = 0; attempt < 30 && (contact is null || send is null); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(400, cancellationToken);
            contact = FindByName(window, recipientName);
            send = FindSendButton(window);
        }
        if (contact is not null && send is not null && send.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) && pattern is InvokePattern invoke)
        {
            invoke.Invoke();
            return;
        }

        throw new InvalidOperationException($"Sicherheitsabbruch: Kontakt „{recipientName}“ oder Senden-Button konnte nicht eindeutig geprüft werden.");
    }

    private static AutomationElement? FindWhatsAppWindow()
    {
        foreach (var process in Process.GetProcessesByName("WhatsApp.Root"))
        {
            if (process.MainWindowHandle == IntPtr.Zero) continue;
            try
            {
                var element = AutomationElement.FromHandle(process.MainWindowHandle);
                if (element.Current.Name.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase)) return element;
            }
            catch (ElementNotAvailableException) { }
        }
        return null;
    }

    private static AutomationElement? FindByName(AutomationElement root, string expected)
    {
        var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        return all.Cast<AutomationElement>().FirstOrDefault(x =>
        {
            try { return x.Current.Name.Contains(expected, StringComparison.OrdinalIgnoreCase); }
            catch (ElementNotAvailableException) { return false; }
        });
    }

    private static AutomationElement? FindSendButton(AutomationElement root)
    {
        var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        return buttons.Cast<AutomationElement>().FirstOrDefault(x =>
        {
            try { var n=x.Current.Name; return n.Equals("Senden",StringComparison.OrdinalIgnoreCase)||n.Equals("Send",StringComparison.OrdinalIgnoreCase); }
            catch (ElementNotAvailableException) { return false; }
        });
    }
    private static bool IsWebViewContentHidden(AutomationElement root)
    {
        var all=root.FindAll(TreeScope.Descendants,Condition.TrueCondition).Cast<AutomationElement>().ToList();
        var names=all.Select(x=>{try{return x.Current.Name;}catch{return "";}}).Where(x=>!string.IsNullOrWhiteSpace(x)).ToList();
        return names.Count<=12 && names.All(x=>x.Contains("WhatsApp",StringComparison.OrdinalIgnoreCase)||x is "Minimize" or "Restore" or "Close" or "Minimieren" or "Wiederherstellen" or "Schließen" or "System" or "Systemmenüleiste" or "AppWindow Custom Title Bar" or "Non Client Input Sink Window");
    }
}
