using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Tooling.Handles;

namespace VsIdeBridge.Infrastructure;

/// <summary>
/// Session-scoped service that wraps a <see cref="HandleRegistry"/> and provides
/// VS-side producer and consumer helpers for bridge commands.
///
/// <para><b>Producing commands</b> call <see cref="RegisterDiagnosticRows"/>,
/// <see cref="RegisterSearchHits"/>, <see cref="RegisterFileMatches"/>, or
/// <see cref="RegisterFileRead"/> to annotate their result items in-place with a
/// <c>"handle"</c> field. Each call appends new instances — handles are never
/// overwritten or cleared, so models can safely hold handles across tool calls.</para>
///
/// <para><b>Consuming commands</b> need zero individual changes — handle resolution is
/// injected transparently into <c>PatchService.ResolveFilePath</c> and
/// <c>DocumentService.ResolveDocumentPath</c>.</para>
/// </summary>
internal sealed class HandleService
{
    private readonly HandleRegistry _registry = new();

    // Matches the path token after any "*** Add/Delete/Update File: " directive.
    private static readonly Regex _patchFileDirective = new(
        @"(?<=\*\*\* (?:Add|Delete|Update) File: )([^\r\n]+)",
        RegexOptions.Compiled);

    // ── Producer API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Stamps each JObject in <paramref name="rows"/> with a <c>"handle"</c> field.
    /// Rows must contain a <c>"file"</c> key (error-list format) and optionally
    /// <c>"line"</c>, <c>"column"</c>, <c>"severity"</c>, <c>"code"</c>,
    /// <c>"message"</c>, and <c>"project"</c>.
    /// Existing handles of the same kind are retained — counters never reset.
    /// </summary>
    public void RegisterDiagnosticRows(HandleKind kind, JArray rows)
    {
        foreach (JObject row in rows.OfType<JObject>())
        {
            string  path     = row["file"]?.Value<string>()     ?? string.Empty;
            int?    line     = row["line"]?.Value<int?>();
            int?    col      = row["column"]?.Value<int?>();
            string? severity = row["severity"]?.Value<string>();
            string? code     = row["code"]?.Value<string>();
            string? message  = row["message"]?.Value<string>();
            string? project  = row["project"]?.Value<string>();
            DiagnosticHandle handle = _registry.RegisterDiagnostic(kind, path, line, col, severity, code, message, project);
            row["handle"] = handle.Id;
        }
    }

    /// <summary>
    /// Stamps each JObject in <paramref name="matches"/> with a <c>"handle"</c> field.
    /// Matches must contain a <c>"path"</c> key and optionally <c>"line"</c>,
    /// <c>"column"</c>, and <c>"preview"</c>.
    /// Existing SearchHit handles are retained — counters never reset.
    /// </summary>
    public void RegisterSearchHits(JArray matches)
    {
        foreach (JObject match in matches.OfType<JObject>())
        {
            string  path    = match["path"]?.Value<string>()    ?? string.Empty;
            int?    line    = match["line"]?.Value<int?>();
            int?    col     = match["column"]?.Value<int?>();
            string? preview = match["preview"]?.Value<string>();
            SearchHitHandle handle = _registry.RegisterSearchHit(path, line, col, preview);
            match["handle"] = handle.Id;
        }
    }

    /// <summary>
    /// Stamps each JObject in <paramref name="matches"/> with a <c>"handle"</c> field.
    /// Matches must contain a <c>"path"</c> key and optionally <c>"name"</c> and
    /// <c>"project"</c>.
    /// Existing FileMatch handles are retained — counters never reset.
    /// </summary>
    public void RegisterFileMatches(JArray matches)
    {
        foreach (JObject match in matches.OfType<JObject>())
        {
            string  path    = match["path"]?.Value<string>()    ?? string.Empty;
            string  name    = match["name"]?.Value<string>()    ?? System.IO.Path.GetFileName(path);
            string? project = match["project"]?.Value<string>();
            FileMatchHandle handle = _registry.RegisterFileMatch(path, name, project);
            match["handle"] = handle.Id;
        }
    }

    /// <summary>
    /// Registers a single file path (e.g. from a <c>read_file</c> call) and returns
    /// the new <c>f:</c> handle. Call this when a file is first accessed by full path
    /// so subsequent <c>apply_diff</c> calls can reference it by handle instead.
    /// </summary>
    public FileMatchHandle RegisterFileRead(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        return _registry.RegisterFileMatch(path, name);
    }

    // ── Consumer API ──────────────────────────────────────────────────────────

    /// <inheritdoc cref="HandleRegistry.IsHandle"/>
    public static bool IsHandle(string? value) => HandleRegistry.IsHandle(value);

    /// <summary>
    /// Returns <see langword="true"/> and sets <paramref name="entry"/> when
    /// <paramref name="id"/> is a registered handle.
    /// Returns <see langword="false"/> for plain paths so callers can fall through to
    /// normal resolution logic.
    /// </summary>
    public bool TryResolve(string id, out HandleEntry? entry) =>
        _registry.TryGet(id, out entry);

    /// <summary>
    /// Resolves <paramref name="fileOrHandle"/> to an absolute path.
    /// Plain paths are returned unchanged; registered handles resolve to their stored path;
    /// unrecognised handle IDs throw a <see cref="CommandErrorException"/> with a clear
    /// recovery instruction.
    /// </summary>
    public string ResolveFilePath(string fileOrHandle)
    {
        if (!HandleRegistry.IsHandle(fileOrHandle)) return fileOrHandle;
        if (TryResolve(fileOrHandle, out HandleEntry? entry) && entry is not null) return entry.Path;
        throw new CommandErrorException("unknown_handle",
            $"Handle '{fileOrHandle}' is not registered. " +
            "Re-run the originating search or diagnostic tool to get a fresh handle.");
    }

    // ── Patch rewriter ────────────────────────────────────────────────────────

    /// <summary>
    /// Rewrites any handle IDs embedded in apply_diff patch text (on
    /// <c>*** Add/Delete/Update File: &lt;handle&gt;</c> lines) with their registered
    /// absolute paths. Called once on the raw patch string before
    /// <c>ApplyEditorPatchAsync</c>. Throws <see cref="CommandErrorException"/> for
    /// unrecognised handles.
    /// </summary>
    public string RewritePatch(string patchText) =>
        _patchFileDirective.Replace(patchText, m =>
        {
            string token = m.Value.TrimEnd();
            if (!HandleRegistry.IsHandle(token)) return m.Value;
            if (TryResolve(token, out HandleEntry? entry) && entry is not null) return entry.Path;
            throw new CommandErrorException("unknown_handle",
                $"Handle '{token}' in patch text is not registered. " +
                "Re-run the originating search or diagnostic tool to get a fresh handle.");
        });

    // ── Position helper ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the embedded (line, column) position stored in the handle, or
    /// <c>(null, null)</c> for a plain path or unregistered handle.
    /// Used by the read_file command to auto-anchor the view when no explicit
    /// <c>start_line</c> is provided.
    /// </summary>
    public (int? Line, int? Column) GetEmbeddedPosition(string? fileOrHandle)
    {
        if (fileOrHandle is not null
            && HandleRegistry.IsHandle(fileOrHandle)
            && TryResolve(fileOrHandle, out HandleEntry? entry)
            && entry is not null)
        {
            return entry switch
            {
                DiagnosticHandle d => (d.Line, d.Column),
                SearchHitHandle  h => (h.Line, h.Column),
                _                  => (null, null),
            };
        }
        return (null, null);
    }
}
