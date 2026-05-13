using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VsIdeBridgeService;

internal sealed class StdioHostLease : IDisposable
{
    private const string LeaseDirectoryName = "stdio-host-leases";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SupersedeIdleThreshold = TimeSpan.FromSeconds(20);
    private static long LastActivityUtcTicks = DateTime.UtcNow.Ticks;
    private static int ActiveRequestCount;

    private readonly int _currentPid;
    private readonly int _parentPid;
    private readonly string _leasePath;
    private readonly string _leaseToken;
    private readonly DateTime _leaseCreatedUtc;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _monitorTask;
    private bool _reclaimedLeaseLogged;

    private StdioHostLease(int currentPid, int parentPid, string leasePath, string leaseToken, DateTime leaseCreatedUtc)
    {
        _currentPid = currentPid;
        _parentPid = parentPid;
        _leasePath = leasePath;
        _leaseToken = leaseToken;
        _leaseCreatedUtc = leaseCreatedUtc;
        _monitorTask = Task.Run(MonitorLoopAsync);
    }

    public static IDisposable BeginActivity()
    {
        MarkActivity();
        Interlocked.Increment(ref ActiveRequestCount);
        return new ActivityScope();
    }

    public static void MarkActivity()
    {
        Interlocked.Exchange(ref LastActivityUtcTicks, DateTime.UtcNow.Ticks);
    }

    public static StdioHostLease? TryCreate()
    {
        int currentPid = Environment.ProcessId;
        int parentPid = TryGetParentProcessId(currentPid);
        if (parentPid <= 0)
        {
            McpServerLog.Write($"stdio host lease inactive; parent pid not found for pid {currentPid}");
            return null;
        }

        string leaseDirectory = GetLeaseDirectory();
        string leasePath = Path.Combine(leaseDirectory, $"mcp-parent-{parentPid}.lease");
        DateTime leaseCreatedUtc = DateTime.UtcNow;
        string leaseToken = $"{currentPid}|{leaseCreatedUtc:O}";

        try
        {
            Directory.CreateDirectory(leaseDirectory);
            File.WriteAllText(leasePath, leaseToken);
            McpServerLog.Write($"stdio host lease acquired pid={currentPid} parentPid={parentPid} lease={leasePath}");
        }
        catch (IOException ex)
        {
            McpServerLog.WriteException($"stdio host lease file unavailable at '{leasePath}'", ex);
            leasePath = string.Empty;
            leaseToken = string.Empty;
        }
        catch (UnauthorizedAccessException ex)
        {
            McpServerLog.WriteException($"stdio host lease file unavailable at '{leasePath}'", ex);
            leasePath = string.Empty;
            leaseToken = string.Empty;
        }
        catch (NotSupportedException ex)
        {
            McpServerLog.WriteException($"stdio host lease file unavailable at '{leasePath}'", ex);
            leasePath = string.Empty;
            leaseToken = string.Empty;
        }

        return new StdioHostLease(currentPid, parentPid, leasePath, leaseToken, leaseCreatedUtc);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _monitorTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            McpServerLog.WriteException("failed to wait for stdio host monitor shutdown", ex);
        }
        catch (ObjectDisposedException ex)
        {
            McpServerLog.WriteException("failed to wait for stdio host monitor shutdown", ex);
        }
        finally
        {
            TryDeleteOwnedLease();
            _cts.Dispose();
        }
    }

    private async Task MonitorLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (!IsProcessAlive(_parentPid))
            {
                McpServerLog.Write($"stdio host parent {_parentPid} exited; terminating pid {_currentPid}");
                Environment.Exit(0);
                return;
            }

            if (IsLeaseSuperseded())
            {
                McpServerLog.Write(
                    $"stdio host lease superseded for parent {_parentPid}; terminating pid {_currentPid}");
                Environment.Exit(0);
                return;
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

    private bool IsLeaseSuperseded()
    {
        if (string.IsNullOrWhiteSpace(_leasePath))
        {
            return false;
        }

        try
        {
            if (!File.Exists(_leasePath))
            {
                return false;
            }

            string currentToken = File.ReadAllText(_leasePath).Trim();
            if (currentToken.Length == 0
                || string.Equals(currentToken, _leaseToken, StringComparison.Ordinal))
            {
                return false;
            }

            if (TryGetLeaseCreatedUtc(currentToken, out DateTime currentLeaseCreatedUtc)
                && currentLeaseCreatedUtc < _leaseCreatedUtc)
            {
                return true;
            }

            if (IsRecentlyActive())
            {
                ReclaimLease();
                return false;
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsRecentlyActive()
    {
        if (Volatile.Read(ref ActiveRequestCount) > 0)
        {
            return true;
        }

        long ticks = Interlocked.Read(ref LastActivityUtcTicks);
        DateTime lastActivityUtc = new(ticks, DateTimeKind.Utc);
        return DateTime.UtcNow - lastActivityUtc <= SupersedeIdleThreshold;
    }

    private void ReclaimLease()
    {
        try
        {
            File.WriteAllText(_leasePath, _leaseToken);
            if (!_reclaimedLeaseLogged)
            {
                _reclaimedLeaseLogged = true;
                McpServerLog.Write(
                    $"stdio host lease reclaim pid={_currentPid} parentPid={_parentPid}; active host retained");
            }
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"StdioHostLease.ReclaimLease failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"StdioHostLease.ReclaimLease failed: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            System.Diagnostics.Debug.WriteLine($"StdioHostLease.ReclaimLease failed: {ex.Message}");
        }
    }

    private static bool TryGetLeaseCreatedUtc(string leaseToken, out DateTime createdUtc)
    {
        string[] parts = leaseToken.Split('|');
        if (parts.Length == 2
            && DateTime.TryParse(parts[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed))
        {
            createdUtc = parsed.ToUniversalTime();
            return true;
        }

        createdUtc = default;
        return false;
    }

    private void TryDeleteOwnedLease()
    {
        if (string.IsNullOrWhiteSpace(_leasePath))
        {
            return;
        }

        try
        {
            if (File.Exists(_leasePath)
                && string.Equals(File.ReadAllText(_leasePath).Trim(), _leaseToken, StringComparison.Ordinal))
            {
                File.Delete(_leasePath);
                McpServerLog.Write($"stdio host lease released pid={_currentPid} parentPid={_parentPid}");
            }
        }
        catch (IOException ex)
        {
            McpServerLog.WriteException($"failed to release stdio host lease '{_leasePath}'", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            McpServerLog.WriteException($"failed to release stdio host lease '{_leasePath}'", ex);
        }
        catch (NotSupportedException ex)
        {
            McpServerLog.WriteException($"failed to release stdio host lease '{_leasePath}'", ex);
        }
    }

    private static string GetLeaseDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "VsIdeBridge", LeaseDirectoryName);
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
        IntPtr snapshot = StdioProcessSnapshotInterop.CreateToolhelp32Snapshot(StdioProcessSnapshotInterop.Th32csSnapprocess, 0);
        if (snapshot == StdioProcessSnapshotInterop.InvalidHandleValue)
        {
            return 0;
        }

        try
        {
            StdioProcessSnapshotInterop.PROCESSENTRY32 entry = new()
            {
                dwSize = (uint)Marshal.SizeOf<StdioProcessSnapshotInterop.PROCESSENTRY32>(),
            };

            if (!StdioProcessSnapshotInterop.Process32First(snapshot, ref entry))
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
            while (StdioProcessSnapshotInterop.Process32Next(snapshot, ref entry));

            return 0;
        }
        finally
        {
            StdioProcessSnapshotInterop.CloseHandle(snapshot);
        }
    }

    private sealed class ActivityScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                MarkActivity();
                Interlocked.Decrement(ref ActiveRequestCount);
            }
        }
    }
}
