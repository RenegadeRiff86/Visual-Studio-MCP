using System;

namespace VsIdeBridge.Tooling.Handles;

/// <summary>
/// Base class for all handle entries stored in a <see cref="HandleRegistry"/>.
/// Each instance is immutable and uniquely identified by <see cref="Id"/>.
/// </summary>
public abstract class HandleEntry
{
    protected HandleEntry(string id, HandleKind kind, string path)
    {
        Id        = id;
        Kind      = kind;
        Path      = path;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Short handle ID, e.g. "h:3" or "e:12".</summary>
    public string Id { get; }

    /// <summary>Result kind that produced this handle.</summary>
    public HandleKind Kind { get; }

    /// <summary>Absolute file-system path associated with this handle.</summary>
    public string Path { get; }

    /// <summary>UTC time at which this handle was registered.</summary>
    public DateTimeOffset CreatedAt { get; }
}
