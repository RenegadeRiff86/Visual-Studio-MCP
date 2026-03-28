using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif
using System.Text;
using System.Threading;

namespace VsIdeBridge.Services;

#if NET5_0_OR_GREATER
[SupportedOSPlatform("windows")]
#endif
internal sealed class MemoryDiscoveryStore : IDisposable
{
    private const string DefaultMapName = @"Local\VsIdeBridge.Discovery.v1";
    private const string DefaultMutexName = @"Local\VsIdeBridge.Discovery.v1.mutex";
    private const int DefaultCapacityBytes = 1024 * 1024;
    private const int LengthPrefixBytes = sizeof(int);
    private static readonly TimeSpan EntryTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly string _mapName;
    private readonly string _mutexName;
    private readonly int _capacityBytes;
    private readonly TimeSpan _lockTimeout;
    private readonly Func<string, Mutex> _mutexFactory;
    private readonly Func<string, int, MemoryMappedFile> _mapFactory;
    private readonly Lazy<MemoryMappedFile?> _sharedMap;

    public MemoryDiscoveryStore()
        : this(
            DefaultMapName,
            DefaultMutexName,
            DefaultCapacityBytes,
            DefaultLockTimeout,
            static name => new Mutex(false, name),
            static (name, capacity) => MemoryMappedFile.CreateOrOpen(name, capacity, MemoryMappedFileAccess.ReadWrite))
    {
    }

    internal MemoryDiscoveryStore(
        string mapName,
        string mutexName,
        int capacityBytes,
        TimeSpan lockTimeout,
        Func<string, Mutex> mutexFactory,
        Func<string, int, MemoryMappedFile> mapFactory)
    {
        _mapName = string.IsNullOrWhiteSpace(mapName) ? DefaultMapName : mapName;
        _mutexName = string.IsNullOrWhiteSpace(mutexName) ? DefaultMutexName : mutexName;
        _capacityBytes = capacityBytes > 0 ? capacityBytes : DefaultCapacityBytes;
        _lockTimeout = lockTimeout > TimeSpan.Zero ? lockTimeout : DefaultLockTimeout;
        _mutexFactory = mutexFactory ?? throw new ArgumentNullException(nameof(mutexFactory));
        _mapFactory = mapFactory ?? throw new ArgumentNullException(nameof(mapFactory));
        _sharedMap = new Lazy<MemoryMappedFile?>(CreateSharedMap, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public void Upsert(object discoveryRecord)
    {
        JObject discoveryEntry = JObject.FromObject(discoveryRecord);
        discoveryEntry["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");

        ExecuteWithStore(root =>
        {
            JArray items = GetItems(root);
            PurgeExpired(items);

            string instanceId = discoveryEntry["instanceId"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(instanceId))
            {
                JObject? existing = items
                    .OfType<JObject>()
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate["instanceId"]?.ToString(), instanceId, StringComparison.OrdinalIgnoreCase));

                if (existing is not null)
                    existing.Replace(discoveryEntry);
                else
                    items.Add(discoveryEntry);
            }
        });
    }

    public void Remove(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        ExecuteWithStore(root =>
        {
            JArray items = GetItems(root);
            JObject[] stale = items
                .OfType<JObject>()
                .Where(candidate =>
                    string.Equals(candidate["instanceId"]?.ToString(), instanceId, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (JObject staleRecord in stale)
            {
                staleRecord.Remove();
            }
        });
    }

    private static JArray GetItems(JObject root)
    {
        JArray? items = root["items"] as JArray;
        if (items is not null)
        {
            return items;
        }

        items = [];
        root["items"] = items;
        return items;
    }

    private static void PurgeExpired(JArray items)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.Subtract(EntryTtl);
        JObject[] staleItems = items
            .OfType<JObject>()
            .Where(item =>
            {
                string? updatedAtUtc = item["updatedAtUtc"]?.ToString();
                if (string.IsNullOrWhiteSpace(updatedAtUtc))
                {
                    return true;
                }

                return !DateTimeOffset.TryParse(updatedAtUtc, out DateTimeOffset parsed) || parsed < cutoff;
            })
            .ToArray();

        foreach (JObject stale in staleItems)
        {
            stale.Remove();
        }
    }

    private void ExecuteWithStore(Action<JObject> mutate)
    {
        Mutex? mutex = null;
        bool hasLock = false;
        try
        {
            Trace($"ExecuteWithStore start map='{_mapName}' mutex='{_mutexName}'.");
            mutex = _mutexFactory(_mutexName);
            try
            {
                hasLock = mutex.WaitOne(_lockTimeout);
            }
            catch (AbandonedMutexException)
            {
                hasLock = true;
            }

            if (!hasLock)
            {
                Trace("ExecuteWithStore lock timeout.");
                return;
            }

            MemoryMappedFile? map = _sharedMap.Value;
            if (map is null)
            {
                Trace("ExecuteWithStore shared map unavailable.");
                return;
            }

            using MemoryMappedViewStream view = map.CreateViewStream(0, _capacityBytes, MemoryMappedFileAccess.ReadWrite);
            JObject root = ReadRoot(view, _capacityBytes);
            mutate(root);
            WriteRoot(view, root, _capacityBytes);
            Trace("ExecuteWithStore write completed.");
        }
        catch (Exception ex)
        {
            // Best-effort store. Discovery JSON remains the compatibility fallback.
            Trace($"ExecuteWithStore error: {ex}");
        }
        finally
        {
            if (hasLock && mutex is not null)
            {
                ReleaseMutexSafely(mutex);
                mutex?.Dispose();
            }
        }
    }

    private static void ReleaseMutexSafely(Mutex mutex)
    {
        try
        {
            mutex.ReleaseMutex();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Mutex release failed: {ex.Message}");
        }
    }

    private MemoryMappedFile? CreateSharedMap()
    {
        try
        {
            return _mapFactory(_mapName, _capacityBytes);
        }
        catch (Exception ex)
        {
            Trace($"CreateSharedMap error: {ex}");
            return null;
        }
    }

    private static void Trace(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("VS_IDE_BRIDGE_TRACE_DISCOVERY"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            string directory = Path.Combine(Path.GetTempPath(), "vs-ide-bridge");
            Directory.CreateDirectory(directory);
            string logPath = Path.Combine(directory, "memory-discovery-trace.log");
            File.AppendAllText(logPath, $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Trace log write failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (!_sharedMap.IsValueCreated)
        {
            return;
        }

        try
        {
            _sharedMap.Value?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Shared map dispose failed: {ex.Message}");
        }
    }

    private static JObject ReadRoot(MemoryMappedViewStream view, int capacityBytes)
    {
        view.Position = 0;
        byte[] lenBuffer = new byte[LengthPrefixBytes];
        int bytesRead = view.Read(lenBuffer, 0, lenBuffer.Length);
        if (bytesRead < lenBuffer.Length)
        {
            return new JObject { ["items"] = new JArray() };
        }

        int payloadLength = BitConverter.ToInt32(lenBuffer, 0);
        if (payloadLength <= 0 || payloadLength > capacityBytes - LengthPrefixBytes)
        {
            return new JObject { ["items"] = new JArray() };
        }

        byte[] payload = new byte[payloadLength];
        bytesRead = view.Read(payload, 0, payload.Length);
        if (bytesRead != payloadLength)
        {
            return new JObject { ["items"] = new JArray() };
        }

        try
        {
            return JObject.Parse(Utf8NoBom.GetString(payload));
        }
        catch
        {
            return new JObject { ["items"] = new JArray() };
        }
    }

    private static void WriteRoot(MemoryMappedViewStream view, JObject root, int capacityBytes)
    {
        root["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");

        byte[] payload = Utf8NoBom.GetBytes(root.ToString());
        if (payload.Length > capacityBytes - LengthPrefixBytes)
        {
            // Keep dropping oldest entries until the payload fits.
            List<JObject> items = GetItems(root)
                .OfType<JObject>()
                .OrderBy(item => item["updatedAtUtc"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (JObject oldestRecord in items)
            {
                oldestRecord.Remove();
                payload = Utf8NoBom.GetBytes(root.ToString());
                if (payload.Length <= capacityBytes - LengthPrefixBytes) break;
            }
        }

        if (payload.Length > capacityBytes - LengthPrefixBytes)
        {
            return;
        }

        view.Position = 0;
        byte[] length = BitConverter.GetBytes(payload.Length);
        view.Write(length, 0, length.Length);
        view.Write(payload, 0, payload.Length);

        int remaining = capacityBytes - LengthPrefixBytes - payload.Length;
        if (remaining > 0)
        {
            byte[] zeros = new byte[Math.Min(remaining, 4096)];
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, zeros.Length);
                view.Write(zeros, 0, chunk);
                remaining -= chunk;
            }
        }

        view.Flush();
    }
}
