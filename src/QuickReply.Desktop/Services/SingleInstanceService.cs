using System.Diagnostics;
using System.Threading;

namespace QuickReply.Desktop.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\QuickReplyTool_SingleInstance";
    private const string ActivateEventName = @"Local\QuickReplyTool_Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _waitHandle;
    private bool _ownsMutex;

    public bool EnsureSingleInstance(Action activateExistingInstance)
    {
        var currentProcess = Process.GetCurrentProcess();
        var currentPath = GetProcessPathSafe(currentProcess);
        var processName = currentProcess.ProcessName;

        _mutex = new Mutex(false, MutexName);
        _ownsMutex = _mutex.WaitOne(0, false);

        if (!_ownsMutex)
        {
            var siblingProcesses = GetSiblingProcesses(processName, currentProcess.Id);
            var samePathProcess = siblingProcesses.FirstOrDefault(process =>
                string.Equals(GetProcessPathSafe(process), currentPath, StringComparison.OrdinalIgnoreCase));

            if (samePathProcess is not null)
            {
                SignalExistingInstance();
                return false;
            }

            foreach (var process in siblingProcesses)
            {
                TryTerminateProcess(process);
            }

            _ownsMutex = TryAcquireMutexAfterCleanup();
            if (!_ownsMutex)
            {
                SignalExistingInstance();
                return false;
            }
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _waitHandle = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            static (state, _) =>
            {
                if (state is Action callback)
                {
                    callback();
                }
            },
            activateExistingInstance,
            -1,
            false);

        return true;
    }

    public void Dispose()
    {
        _waitHandle?.Unregister(null);
        _waitHandle = null;

        _activateEvent?.Dispose();
        _activateEvent = null;

        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch
            {
            }
        }

        _mutex?.Dispose();
        _mutex = null;
        _ownsMutex = false;
    }

    public static void RequestExistingInstanceActivation()
    {
        SignalExistingInstance();
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ActivateEventName);
            handle.Set();
        }
        catch
        {
        }
    }

    private bool TryAcquireMutexAfterCleanup()
    {
        if (_mutex is null)
        {
            return false;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (_mutex.WaitOne(150, false))
                {
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<Process> GetSiblingProcesses(string processName, int currentProcessId)
    {
        return Process.GetProcessesByName(processName)
            .Where(process => process.Id != currentProcessId)
            .ToList();
    }

    private static string GetProcessPathSafe(Process process)
    {
        try
        {
            return process.MainModule?.FileName?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(true);
            process.WaitForExit(5000);
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}
