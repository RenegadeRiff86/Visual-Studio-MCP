using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Infrastructure;

// ── Domain ───────────────────────────────────────────────────────────────────

internal enum HandleKind { Error, Warning, Message, FileMatch, SearchHit }

internal sealed record HandleEntry(HandleKind Kind, string Path, int? Line, int? Column);

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Session-scoped service that maps short handle IDs (e.g. "h:3", "e:1") to file locations.
/// One instance lives on <see cref="IdeBridgeRuntime"/>; all bridge commands share it.
///
/// <para><b>Producing commands</b> call <see cref="RegisterDiagnosticRows"/>,
/// <see cref="RegisterSearchHits"/>, or <see cref="RegisterFileMatches"/> to annotate
/// their JArray result items in-place with a "handle" field.</para>
///
/// <para><b>Consuming commands</b> need zero individual changes — handle resolution is
/// injected transparently into <c>PatchService.ResolveFilePath</c> and
/// <c>DocumentService.ResolveDocumentPath</c>.</para>
/// </summary>
internal sealed class HandleService
{
    // ── Private registry (implementation detail) ──────────────────────────────

    private sealed class HandleRegistry
    {
        private readonly Dictionary<string, HandleEntry> _handles = new(StringComparer.Ordinal);
        private readonly Dictionary<HandleKind, int> _counters = [];
        private readonly object _lock = new();

        public string Register(HandleKind kind, string path, int? line, int? column)
        {
            lock (_lock)
            {
                _counters.TryGetValue(kind, out int nextSeq);
                nextSeq++;
                _counters[kind] = nextSeq;
                string id = $"{PrefixFor(kind)}:{nextSeq}";
                _handles[id] = new HandleEntry(kind, path, line, column);
                return id;
            }
        }

        public bool TryGet(string id, out HandleEntry entry)
        {
            lock (_lock) { return _handles.TryGetValue(id, out entry!); }
        }

        public void ClearKind(HandleKind kind)
        {
            char prefix = PrefixFor(kind);
            lock (_lock)
            {
                foreach (string key in _handles.Keys
                    .Where(k => k.Length > 2 && k[0] == prefix && k[1] == ':')
                    .ToList())
                {
                    _handles.Remove(key);
                }
                _counters[kind] = 0;
            }
        }

        private static char PrefixFor(HandleKind kind) => kind switch
        {
            HandleKind.Error     => 'e',
            HandleKind.Warning   => 'w',
            HandleKind.Message   => 'm',
            HandleKind.FileMatch => 'f',
            HandleKind.SearchHit => 'h',
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly HandleRegistry _registry = new();

    // Matches the path token after any "*** Add/Delete/Update File: " directive.
    private static readonly Regex _patchFileDirective = new(
        @"(?<=\*\*\* (?:Add|Delete|Update) File: )([^\r\n]+)",
        RegexOptions.Compiled);

    // ── Producer API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Clears existing handles for <paramref name="kind"/>, then stamps each JObject in
    /// <paramref name="rows"/> with a <c>"handle"</c> field. Rows must contain a
    /// <c>"file"</c> key (error-list format) and optionally <c>"line"</c>/<c>"column"</c>.
    /// </summary>
    public void RegisterDiagnosticRows(HandleKind kind, JArray rows)
    {
        _registry.ClearKind(kind);
        foreach (JObject row in rows.OfType<JObject>())
        {
            string path = row["file"]?.Value<string>() ?? string.Empty;
            int?   line = row["line"]?.Value<int?>();
            int?   col  = row["column"]?.Value<int?>();
            row["handle"] = _registry.Register(kind, path, line, col);
        }
    }

    /// <summary>
    /// Clears existing SearchHit handles, then stamps each JObject in
    /// <paramref name="matches"/> with a <c>"handle"</c> field.
    /// Matches must contain a <c>"path"</c> key and optionally <c>"line"</c>/<c>"column"</c>.
    /// </summary>
    public void RegisterSearchHits(JArray matches)
    {
        _registry.ClearKind(HandleKind.SearchHit);
        foreach (JObject match in matches.OfType<JObject>())
        {
            string path = match["path"]?.Value<string>() ?? string.Empty;
            int?   line = match["line"]?.Value<int?>();
            int?   col  = match["column"]?.Value<int?>();
            match["handle"] = _registry.Register(HandleKind.SearchHit, path, line, col);
        }
    }

    /// <summary>
    /// Clears existing FileMatch handles, then stamps each JObject in
    /// <paramref name="matches"/> with a <c>"handle"</c> field.
    /// Matches must contain a <c>"path"</c> key.
    /// </summary>
    public void RegisterFileMatches(JArray matches)
    {
        _registry.ClearKind(HandleKind.FileMatch);
        foreach (JObject match in matches.OfType<JObject>())
        {
            string path = match["path"]?.Value<string>() ?? string.Empty;
            match["handle"] = _registry.Register(HandleKind.FileMatch, path, null, null);
        }
    }

    // ── Consumer API (used by PatchService and DocumentService) ───────────────

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> looks like a handle ID
    /// (single letter, colon, one or more digits — e.g. "h:3", "e:12").
    /// Windows drive paths like "C:\..." are correctly rejected because "\Users\..." is not
    /// a valid integer.
    /// </summary>
    public static bool IsHandle(string? value) =>
        value is { Length: > 2 } && value[1] == ':' && int.TryParse(value.Substring(2), out _);

    /// <summary>
    /// Returns <see langword="true"/> and sets <paramref name="entry"/> when
    /// <paramref name="id"/> is a registered handle.
    /// Returns <see langword="false"/> for plain paths so callers can fall through to
    /// normal resolution logic.
    /// </summary>
    public bool TryResolve(string id, out HandleEntry entry) =>
        _registry.TryGet(id, out entry);

    /// <summary>
    /// Resolves <paramref name="fileOrHandle"/> to an absolute path.
    /// If it is a plain path it is returned unchanged; if it is a handle the registered
    /// path is returned; if it is an unknown handle a <see cref="CommandErrorException"/>
    /// is thrown so the model receives a clear, actionable error message.
    /// </summary>
    public string ResolveFilePath(string fileOrHandle)
    {
        if (!IsHandle(fileOrHandle)) return fileOrHandle;
        if (TryResolve(fileOrHandle, out HandleEntry entry)) return entry.Path;
        throw new CommandErrorException("unknown_handle",
            $"Handle '{fileOrHandle}' is not registered. " +
            "Re-run the originating search or diagnostic tool to refresh handles.");
    }

    // ── Patch rewriter (used by apply_diff before calling PatchService) ───────

    /// <summary>
    /// Rewrites any handle IDs embedded in apply_diff patch text (on
    /// <c>*** Add/Delete/Update File: &lt;handle&gt;</c> lines) with their registered
    /// absolute paths. Called once on the raw patch string before
    /// <c>ApplyEditorPatchAsync</c>. Throws <see cref="CommandErrorException"/> for
    /// unknown handles.
    /// </summary>
    public string RewritePatch(string patchText) =>
        _patchFileDirective.Replace(patchText, m =>
        {
            string token = m.Value.TrimEnd();
            if (!IsHandle(token)) return m.Value;
            if (TryResolve(token, out HandleEntry entry)) return entry.Path;
            throw new CommandErrorException("unknown_handle",
                $"Handle '{token}' in patch text is not registered. " +
                "Re-run the originating search or diagnostic tool to refresh handles.");
        });

    // ── Position helper for read_file auto-anchoring ──────────────────────────

    /// <summary>
    /// Returns the embedded (line, column) position stored in the handle, or
    /// <c>(null, null)</c> for a plain path or unregistered handle.
    /// Used by the read_file command to auto-anchor the view when no explicit
    /// <c>start_line</c> is provided.
    /// </summary>
    public (int? Line, int? Column) GetEmbeddedPosition(string? fileOrHandle)
    {
        if (fileOrHandle is not null
            && IsHandle(fileOrHandle)
            && TryResolve(fileOrHandle, out HandleEntry entry))
        {
            return (entry.Line, entry.Column);
        }
        return (null, null);
    }
}
