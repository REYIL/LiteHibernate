using Microsoft.Win32;

namespace LiteHibernate.Windows;

internal static class AutoStart
{
    internal static void Set(bool enabled)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        if (!enabled)
        {
            // A disabled preference needs no write access when our entry is absent.
            using var existing = Registry.CurrentUser.OpenSubKey(runKey);
            if (existing?.GetValue("LiteHibernate") == null) return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(runKey, true);
        if (enabled) key.SetValue("LiteHibernate", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("LiteHibernate", false);
    }
}
