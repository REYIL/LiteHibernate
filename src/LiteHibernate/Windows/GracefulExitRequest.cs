using System.Runtime.InteropServices;
using System.Text;

namespace LiteHibernate.Windows;

// Restart Manager sends the application shutdown protocol (including tray applications).
// Only an explicitly tracked PID/start-time pair is registered; no files or services.
internal sealed class GracefulExitRequest : IDisposable
{
    private static readonly SemaphoreSlim Sessions = new(8, 8);
    private readonly object sync = new();
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationToken stopToken;
    private readonly IRestartManagerApi api;
    private readonly Action<string, bool>? log;
    private uint? session;
    private bool stopped, finished;
    private readonly CancellationTokenRegistration cancellation;
    internal Task Completion { get; }

    internal GracefulExitRequest(ProcessInfo process, CancellationToken token, Action<string, bool>? log = null, IRestartManagerApi? api = null)
    {
        this.api = api ?? new WindowsRestartManagerApi(); this.log = log;
        stopToken = stop.Token;
        cancellation = token.Register(Stop);
        Completion = Task.Run(() => RunAsync(process));
    }
    private async Task RunAsync(ProcessInfo process)
    {
        var acquired = false;
        try
        {
            // Bound our own sessions and let queued requests be cancelled on timeout/exit.
            await Sessions.WaitAsync(stopToken); acquired = true;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                stopToken.ThrowIfCancellationRequested();
                var error = api.Start(out var handle);
                var operation = "RmStartSession";
                if (error == 0)
                {
                    try
                    {
                        lock (sync) { if (stopped) return; session = handle; }
                        operation = "RmRegisterResources";
                        error = api.Register(handle, process);
                        if (error == 0)
                        {
                            stopToken.ThrowIfCancellationRequested();
                            operation = "RmShutdown";
                            // Zero flags: never bypass the configured force/cancel/continue policy.
                            error = api.Shutdown(handle);
                        }
                    }
                    finally
                    {
                        lock (sync) { session = null; }
                        var endError = api.End(handle);
                        if (endError != 0) log?.Invoke($"RmEndSession: ошибка {endError}; PID {process.Pid}.", true);
                    }
                }
                if (stopToken.IsCancellationRequested) return;
                if (error == 0)
                {
                    log?.Invoke($"Штатное завершение через Restart Manager: {Path.GetFileName(process.Path)}; PID {process.Pid}.", false);
                    return;
                }
                log?.Invoke($"{operation}: ошибка {error}; {Path.GetFileName(process.Path)}; PID {process.Pid}; попытка {attempt + 1}.", true);
                // Retry resource contention; refusals and unsupported apps follow their timeout policy.
                if (error is not (121 or 170 or 353) || attempt == 2) return;
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), stopToken);
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { }
        catch (Exception ex) { log?.Invoke($"Ошибка штатного завершения PID {process.Pid}: {ex}", true); }
        finally
        {
            lock (sync) { finished = true; }
            if (acquired) Sessions.Release();
            stop.Dispose();
        }
    }
    private void Stop()
    {
        lock (sync)
        {
            if (stopped || finished) return;
            stopped = true; stop.Cancel();
            if (session is uint handle)
            {
                var error = api.Cancel(handle);
                if (error is not (0 or 1223)) log?.Invoke($"RmCancelCurrentTask: ошибка {error}.", true);
            }
        }
    }
    public void Dispose() { Stop(); cancellation.Dispose(); }
}

internal interface IRestartManagerApi
{
    uint Start(out uint session);
    uint Register(uint session, ProcessInfo process);
    uint Shutdown(uint session);
    uint Cancel(uint session);
    uint End(uint session);
}

internal sealed class WindowsRestartManagerApi : IRestartManagerApi
{
    public uint Start(out uint session) => RmStartSession(out session, 0, new StringBuilder(33));
    public uint Register(uint session, ProcessInfo process) => RmRegisterResources(session, 0, null, 1,
        [new UniqueProcess { Pid = (uint)process.Pid, CreatedLow = (uint)process.Created, CreatedHigh = (uint)((ulong)process.Created >> 32) }], 0, null);
    public uint Shutdown(uint session) => RmShutdown(session, 0, IntPtr.Zero);
    public uint Cancel(uint session) => RmCancelCurrentTask(session);
    public uint End(uint session) => RmEndSession(session);

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess { public uint Pid, CreatedLow, CreatedHigh; }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern uint RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern uint RmRegisterResources(uint session, uint files, string[]? fileNames,
        uint applications, UniqueProcess[] processes, uint services, string[]? serviceNames);
    [DllImport("rstrtmgr.dll")]
    private static extern uint RmShutdown(uint session, uint flags, IntPtr callback);
    [DllImport("rstrtmgr.dll")]
    private static extern uint RmCancelCurrentTask(uint session);
    [DllImport("rstrtmgr.dll")]
    private static extern uint RmEndSession(uint session);
}
