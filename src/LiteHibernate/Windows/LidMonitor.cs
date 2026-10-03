using System.Runtime.InteropServices;
using System.Windows.Interop;
using LiteHibernate.Infrastructure;

namespace LiteHibernate.Windows;

public sealed class LidMonitor : IDisposable
{
    private readonly HwndSource window;
    private readonly List<IntPtr> notifications = [];
    private IntPtr lidNotification;
    private bool? previous;
    private bool awaitingBaseline = true;
    private readonly Storage? storage;
    public bool HasLid { get; private set; }
    public event Action<bool>? LidChanged;
    public event Action? StateChanged;
    public event Action? SchemeChanged;
    public event Action? Resumed;
    public event Action? SessionEnding;
    public bool? IsOpen => previous;
    public LidMonitor(bool? hasLid = null, Storage? storage = null)
    {
        this.storage = storage;
        HasLid = hasLid ?? DetectLid();
        // A hidden top-level window, not a message-only window, receives power broadcasts.
        window = new HwndSource(new HwndSourceParameters("LiteHibernate.PowerEvents") { Width = 0, Height = 0, WindowStyle = 0 });
        window.AddHook(Hook);
        try
        {
            if (HasLid && !Register(Native.LidState)) HasLid = false;
            Register(Native.ActiveScheme);
        }
        catch { Dispose(); throw; }
    }
    private bool DetectLid()
    {
        var capabilities = new byte[256];
        if (Native.GetPwrCapabilities(capabilities)) return capabilities[2] != 0; // SYSTEM_POWER_CAPABILITIES.LidPresent.
        storage?.Log("Не удалось определить наличие крышки: " + Native.Error("GetPwrCapabilities"), true);
        return false;
    }
    private bool Register(Guid setting)
    {
        var notification = Native.RegisterPowerSettingNotification(window.Handle, in setting, 0);
        if (notification == IntPtr.Zero)
        {
            storage?.Log("Не удалось подписаться на событие питания " + setting + ": " + Native.Error("RegisterPowerSettingNotification"), true);
            return false;
        }
        notifications.Add(notification);
        if (setting == Native.LidState) lidNotification = notification;
        return true;
    }
    private void RefreshLidSubscription()
    {
        if (!HasLid) return;
        // Re-register to obtain the current state after resume. Keep the last known state
        // until Windows supplies it; a baseline notification must never start a scenario.
        awaitingBaseline = true;
        var renewed = Native.RegisterPowerSettingNotification(window.Handle, in Native.LidState, 0);
        if (renewed == IntPtr.Zero)
        {
            awaitingBaseline = false;
            storage?.Log("Не удалось обновить состояние крышки после пробуждения: " + Native.Error("RegisterPowerSettingNotification"), true);
            return;
        }
        var old = lidNotification;
        lidNotification = renewed;
        notifications.Add(renewed);
        notifications.Remove(old);
        Native.UnregisterPowerSettingNotification(old);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message == 0x0218)
        {
            if (wparam.ToInt64() == 0x8013 && lparam != IntPtr.Zero)
            {
                var setting = Marshal.PtrToStructure<Guid>(lparam);
                var length = Marshal.ReadInt32(lparam, 16);
                if (HasLid && setting == Native.LidState && length == 4)
                {
                    var value = Marshal.ReadInt32(lparam, 20);
                    if (value is 0 or 1)
                    {
                        bool open = value == 1;
                        bool changed = previous.HasValue && previous != open && !awaitingBaseline;
                        previous = open;
                        awaitingBaseline = false;
                        StateChanged?.Invoke();
                        if (changed) LidChanged?.Invoke(open);
                    }
                }
                else if (setting == Native.ActiveScheme) SchemeChanged?.Invoke();
            }
            else if (wparam.ToInt64() is 0x0007 or 0x0012)
            {
                RefreshLidSubscription();
                Resumed?.Invoke();
            }
        }
        else if (message == 0x0011) { handled = true; return new IntPtr(1); }
        else if (message == 0x0016 && wparam != IntPtr.Zero) SessionEnding?.Invoke();
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        foreach (var handle in notifications) Native.UnregisterPowerSettingNotification(handle);
        notifications.Clear();
        window.Dispose();
    }
}
