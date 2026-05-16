using System;
using System.Collections.Generic;

namespace VsIdeBridge.Tooling.Handles;

/// <summary>
/// Session-scoped, thread-safe accumulating registry of handle instances.
///
/// <para>Each registration creates a new <see cref="HandleEntry"/> instance with a
/// monotonically-increasing ID — handles are never overwritten and never cleared.
/// This means a model can safely hold an <c>h:3</c> or <c>e:7</c> handle across
/// multiple tool calls without it becoming stale.</para>
///
/// <para>Consuming tools resolve handles via <see cref="TryGet"/> or
/// <see cref="HandleKindHelper.IsHandle"/>.</para>
/// </summary>
public sealed class HandleRegistry
{
    private readonly Dictionary<string, HandleEntry> _handles = new(StringComparer.Ordinal);
    private readonly Dictionary<HandleKind, int>     _counters = [];
    private readonly object                          _lock = new();

    // ── Producers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a new diagnostic result and returns the typed handle instance.
    /// </summary>
    public DiagnosticHandle RegisterDiagnostic(
        HandleKind kind,
        string     path,
        int?       line,
        int?       column,
        string?    severity = null,
        string?    code     = null,
        string?    message  = null,
        string?    project  = null)
    {
        lock (_lock)
        {
            string          id             = NextId(kind);
            DiagnosticHandle diagnosticEntry = new(id, kind, path, line, column, severity, code, message, project);
            _handles[id]                     = diagnosticEntry;
            return diagnosticEntry;
        }
    }

    /// <summary>
    /// Registers a new search-hit result and returns the typed handle instance.
    /// </summary>
    public SearchHitHandle RegisterSearchHit(
        string  path,
        int?    line,
        int?    column,
        string? preview = null)
    {
        lock (_lock)
        {
            string        id         = NextId(HandleKind.SearchHit);
            SearchHitHandle hitEntry = new(id, path, line, column, preview);
            _handles[id]             = hitEntry;
            return hitEntry;
        }
    }

    /// <summary>
    /// Registers a new file-match result and returns the typed handle instance.
    /// </summary>
    public FileMatchHandle RegisterFileMatch(
        string  path,
        string  name,
        string? project = null)
    {
        lock (_lock)
        {
            string         id         = NextId(HandleKind.FileMatch);
            FileMatchHandle fileEntry = new(id, path, name, project);
            _handles[id]              = fileEntry;
            return fileEntry;
        }
    }

    // ── Consumers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Looks up a handle by ID. Returns <see langword="false"/> for plain paths so
    /// callers can fall through to normal path-resolution logic.
    /// </summary>
    public bool TryGet(string id, out HandleEntry? entry)
    {
        lock (_lock)
        {
            bool found = _handles.TryGetValue(id, out HandleEntry? result);
            entry = result;
            return found;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <inheritdoc cref="HandleKindHelper.IsHandle"/>
    public static bool IsHandle(string? value) => HandleKindHelper.IsHandle(value);

    private string NextId(HandleKind kind)
    {
        // Called under _lock
        _counters.TryGetValue(kind, out int seq);
        seq++;
        _counters[kind] = seq;
        return $"{HandleKindHelper.PrefixFor(kind)}:{seq}";
    }
}
