using System.Diagnostics;

namespace LiteHibernate.Windows;

public sealed record ProcessIdentity(int Pid, long Created)
{
    public static ProcessIdentity Current { get; } = ReadCurrent();
    private static ProcessIdentity ReadCurrent() { using var process = Process.GetCurrentProcess(); return FromProcess(process); }
    public static ProcessIdentity FromProcess(Process process)
    {
        if (!Native.GetProcessTimes(process.SafeHandle, out var created, out _, out _, out _))
            throw Native.Error("Не удалось проверить владельца настроек питания.");
        return new(process.Id, created);
    }
    public bool IsAlive()
    {
        using var handle = Native.OpenProcess(0x101000, false, Pid);
        if (handle.IsInvalid)
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (error == 87) return false;
            throw Native.Error("Не удалось проверить владельца журнала питания.", error);
        }
        var wait = Native.WaitForSingleObject(handle, 0);
        if (wait == uint.MaxValue) throw Native.Error("Не удалось проверить владельца журнала питания.");
        if (wait == 0) return false;
        if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _))
            throw Native.Error("Не удалось проверить владельца журнала питания.");
        return created == Created;
    }
}
