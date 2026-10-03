using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LiteHibernate.Windows;

[Flags] public enum AppSource { Installed = 1, Running = 2, Manual = 4 }
public sealed record CatalogEntry(string Name, string? ExecutablePath, AppSource Source);

public sealed class AppCatalog
{
    public IReadOnlyList<CatalogEntry> Installed()
    {
        var result = new List<CatalogEntry>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null) continue;
            foreach (var sub in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(sub);
                    if (key?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)
                        || key.GetValue("SystemComponent") is int system && system == 1) continue;
                    // DisplayIcon can refer to an uninstaller or DLL; never guess from UninstallString.
                    var icon = ExtractExe(key.GetValue("DisplayIcon") as string);
                    if (icon != null && (Path.GetFileName(icon).Contains("unins", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(icon).Contains("setup", StringComparison.OrdinalIgnoreCase))) icon = null;
                    result.Add(new(name, icon, AppSource.Installed));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        // Shortcut resolution uses COM and runs on the UI's STA thread.
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type != null)
        {
            object? shellObject = null;
            try
            {
                shellObject = Activator.CreateInstance(type);
                dynamic shell = shellObject!;
                foreach (var folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
                foreach (var link in SafeLinks(Environment.GetFolderPath(folder)))
                {
                    object? shortcutObject = null;
                    try
                    {
                        shortcutObject = shell.CreateShortcut(link);
                        dynamic shortcut = shortcutObject;
                        string target = shortcut.TargetPath;
                        var exe = ExtractExe(target);
                        if (exe != null && !Path.GetFileName(exe).Contains("unins", StringComparison.OrdinalIgnoreCase))
                            result.Add(new(Path.GetFileNameWithoutExtension(link), exe, AppSource.Installed));
                    }
                    catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { }
                    finally { if (shortcutObject != null) Marshal.FinalReleaseComObject(shortcutObject); }
                }
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { }
            finally { if (shellObject != null) Marshal.FinalReleaseComObject(shellObject); }
        }
        // Remove unresolved entries when a shortcut provided an executable with the same display name.
        var resolvedNames = result.Where(e => e.ExecutablePath != null).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return result.Where(e => e.ExecutablePath != null || !resolvedNames.Contains(e.Name))
            .DistinctBy(e => e.ExecutablePath ?? "name:" + e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static IEnumerable<string> SafeLinks(string root)
    {
        if (!Directory.Exists(root)) return [];
        try { return Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToArray(); }
        catch (IOException) { return []; }
    }
    private static string? ExtractExe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = Environment.ExpandEnvironmentVariables(value.Trim());
        if (path.StartsWith('"'))
        {
            var end = path.IndexOf('"', 1);
            if (end < 0) return null;
            path = path[1..end];
        }
        else
        {
            var end = path.LastIndexOf(",", StringComparison.Ordinal);
            if (end >= 0 && int.TryParse(path[(end + 1)..], out _)) path = path[..end];
        }
        try { return ProcessInventory.IsSelectablePath(path) && File.Exists(path) ? Path.GetFullPath(path) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }
}
