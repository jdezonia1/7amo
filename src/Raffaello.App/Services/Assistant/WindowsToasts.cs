using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Raffaello.App.Services.Assistant;

/// <summary>
/// Windows notifications (Action Center) for an unpackaged desktop app: registers an AppUserModelID under HKCU (display name + icon)
/// once, sets it on the process and shows a toast. Any failure (policy, older Windows) falls back silently to the in-app toast.
/// </summary>
public static class WindowsToasts
{
    public const string AppId = "MOBCO.Raffaello";
    private static bool _ready;

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    private static bool Ensure()
    {
        if (_ready) return true;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
            key.SetValue("DisplayName", "Raffaello");
            var icon = Path.Combine(AppContext.BaseDirectory, "raffaello-toast.png");
            if (!File.Exists(icon))
            {
                var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/raffaello-link.png"));
                if (res != null) { using var f = File.Create(icon); res.Stream.CopyTo(f); }
            }
            if (File.Exists(icon)) key.SetValue("IconUri", icon);
            SetCurrentProcessExplicitAppUserModelID(AppId);
            _ready = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException or COMException) { _ready = false; }
        return _ready;
    }

    private static string X(string s) => SecurityElement.Escape(s) ?? "";

    /// <summary>Shows a Windows notification. Returns false when Windows did not accept it.</summary>
    public static bool Show(string title, string body)
    {
        try
        {
            if (!Ensure()) return false;
            var xml = new XmlDocument();
            xml.LoadXml($"<toast><visual><binding template=\"ToastGeneric\"><text>{X(title)}</text><text>{X(body.Length > 300 ? body[..300] + "..." : body)}</text></binding></visual></toast>");
            ToastNotificationManager.CreateToastNotifier(AppId).Show(new ToastNotification(xml));
            return true;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
