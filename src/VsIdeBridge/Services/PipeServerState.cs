namespace VsIdeBridge.Services;

internal sealed class PipeServerMutableState
{
    public bool DiscoveryWorkerScheduled;
    public bool DiscoveryPurgePending;
    public string? PendingDiscoverySolutionPath;
    public int QueuedCommandCount;
}
