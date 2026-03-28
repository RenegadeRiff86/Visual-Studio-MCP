using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VsIdeBridgeService;

internal sealed class StdioHostLease : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly int _currentPid;
    private readonly int _parentPid;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _monitorTask;

    private StdioHostLease(int currentPid, int parentPid)
    {
        _currentPid = currentPid;
        _parentPid = parentPid;
        _monitorTask = Task.Run(MonitorLoopAsync);
    }

    public static StdioHostLease? TryCreate()
    {
        int currentPid = Environment.ProcessId;
        int parentPid = TryGetParentProcessId(currentPid);
        if (parentPid <= 0)
        {
            return null;
        }

        return new StdioHostLease(currentPid, parentPid);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _monitorTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // best effort shutdown
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private async Task MonitorLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!IsProcessAlive(_parentPid))
                {
                    McpServerLog.Write($"stdio host parent {_parentPid} exited; terminating pid {_currentPid}");
                    Environment.Exit(0);
                    return;
                }
            }
            catch (Exception ex)
            {
                McpServerLog.Write($"stdio host lease monitor error: {ex.Message}");
            }

            try
            {
                await Task.Delay(PollInterval, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static int TryGetParentProcessId(int pid)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
        if (snapshot == InvalidHandleValue)
        {
            return 0;
        }

        try
        {
            PROCESSENTRY32 entry = new()
            {
                dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>(),
            };

            if (!Process32First(snapshot, ref entry))
            {
                return 0;
            }

            do
            {
                if ((int)entry.th32ProcessID == pid)
                {
                    return (int)entry.th32ParentProcessID;
                }
            }
            while (Process32Next(snapshot, ref entry));

            return 0;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static readonly IntPtr InvalidHandleValue = new(-1);
    private const uint Th32csSnapprocess = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
