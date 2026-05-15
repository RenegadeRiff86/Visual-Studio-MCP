using System;

namespace VsIdeBridge.Tooling.Handles;

/// <summary>
/// Classifies the result kind that produced a handle, which determines the ID prefix
/// (e: Error, w: Warning, m: Message, f: FileMatch, h: SearchHit).
/// </summary>
public enum HandleKind
{
    Error,
    Warning,
    Message,
    FileMatch,
    SearchHit,
}

/// <summary>
/// Static helpers shared by producers and consumers without needing a registry instance.
/// </summary>
public static class HandleKindHelper
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> matches the handle
    /// pattern — a single letter, colon, then one or more digits (e.g. "h:3", "e:12").
    /// Windows drive letters like "C:\..." are correctly rejected.
    /// </summary>
    public static bool IsHandle(string? value) =>
        value is { Length: > 2 } && value[1] == ':' && int.TryParse(value.Substring(2), out _);

    /// <summary>Returns the single-character ID prefix for <paramref name="kind"/>.</summary>
    public static char PrefixFor(HandleKind kind) => kind switch
    {
        HandleKind.Error     => 'e',
        HandleKind.Warning   => 'w',
        HandleKind.Message   => 'm',
        HandleKind.FileMatch => 'f',
        HandleKind.SearchHit => 'h',
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
