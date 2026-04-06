using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VsIdeBridge.Services;

internal sealed partial class DocumentService
{
    private static readonly HashSet<vsCMElement> s_outlineKinds =
    [
        vsCMElement.vsCMElementFunction,
        vsCMElement.vsCMElementClass,
        vsCMElement.vsCMElementStruct,
        vsCMElement.vsCMElementEnum,
        vsCMElement.vsCMElementNamespace,
        vsCMElement.vsCMElementInterface,
        vsCMElement.vsCMElementProperty,
        vsCMElement.vsCMElementVariable,
    ];

    public async Task<JObject> GetFileOutlineAsync(DTE2 dte, string? filePath, int maxDepth, string? kindFilter = null)
    {
        (string resolvedPath, JArray symbols, int count, string? note) =
            await GetOutlineDataOnMainThreadAsync(dte, filePath, maxDepth, kindFilter).ConfigureAwait(false);

        JObject outlineResult = new()
        {
            [ResolvedPathProperty] = resolvedPath,
            ["count"] = count,
            ["symbols"] = symbols,
        };
        if (note is not null) outlineResult["note"] = note;
        return outlineResult;
    }

    private static async Task<(string ResolvedPath, JArray Symbols, int Count, string? Note)> GetOutlineDataOnMainThreadAsync(
        DTE2 dte,
        string? filePath,
        int maxDepth,
        string? kindFilter)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        string resolvedPath = ResolveDocumentPath(dte, filePath);

        ProjectItem? projectItem = null;
        try { projectItem = dte.Solution.FindProjectItem(resolvedPath); } catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); }

        JArray symbols = [];
        string? note = projectItem is null
            ? "File is not part of any project or code model is unavailable."
            : TryCollectFileCodeElements(projectItem, symbols, maxDepth, kindFilter);

        int count = symbols.Count;
        await Task.Yield();
        return (resolvedPath, symbols, count, note);
    }

    private static string? TryCollectFileCodeElements(ProjectItem projectItem, JArray symbols, int maxDepth, string? kindFilter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            FileCodeModel? codeModel = projectItem.FileCodeModel;
            if (codeModel?.CodeElements is not null)
            {
                foreach (CodeElement element in codeModel.CodeElements)
                {
                    try { CollectOutlineSymbols(element, symbols, 0, maxDepth, kindFilter); } catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); }
                }

                return null;
            }

            return "No code model available for this file type.";
        }
        catch (COMException ex)
        {
            return $"Code model unavailable: {ex.Message}";
        }
    }

    private static void CollectOutlineSymbols(CodeElement element, JArray symbols, int depth, int maxDepth, string? kindFilter = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (depth > maxDepth) return;

        vsCMElement kind;
        try { kind = element.Kind; } catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); return; }

        string name = string.Empty;
        int startLine = 0, endLine = 0;
        try { name = element.Name ?? string.Empty; } catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); }
        try { startLine = element.StartPoint?.Line ?? 0; } catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); }
        try { endLine = element.EndPoint?.Line ?? 0; } catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); }

        if (s_outlineKinds.Contains(kind))
        {
            string kindName = NormalizeOutlineKind(kind);
            bool matchesFilter = MatchesOutlineKind(kindName, kindFilter);
            if (matchesFilter)
            {
                symbols.Add(new JObject
                {
                    ["name"] = name,
                    ["kind"] = kindName,
                    ["startLine"] = startLine,
                    ["endLine"] = endLine,
                    ["depth"] = depth,
                });
            }
        }

        CodeElements? children = null;
        try
        {
            if (element is CodeNamespace ns) children = ns.Members;
            else if (element is CodeClass cls) children = cls.Members;
            else if (element is CodeStruct st) children = st.Members;
            else if (element is CodeInterface iface) children = iface.Members;
        }
        catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); }

        if (children is null) return;
        foreach (CodeElement child in children)
        {
            try { CollectOutlineSymbols(child, symbols, depth + 1, maxDepth, kindFilter); } catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }

    private static string NormalizeOutlineKind(vsCMElement kind)
    {
        return kind switch
        {
            vsCMElement.vsCMElementFunction => "function",
            vsCMElement.vsCMElementClass => "class",
            vsCMElement.vsCMElementStruct => "struct",
            vsCMElement.vsCMElementEnum => "enum",
            vsCMElement.vsCMElementNamespace => "namespace",
            vsCMElement.vsCMElementInterface => "interface",
            vsCMElement.vsCMElementProperty => "member",
            vsCMElement.vsCMElementVariable => "member",
            _ => kind.ToString().Replace("vsCMElement", string.Empty),
        };
    }

    private static bool MatchesOutlineKind(string normalizedKind, string? kindFilter)
    {
        string? filter = kindFilter?.Trim();
        if (string.IsNullOrWhiteSpace(filter) ||
            string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return filter!.ToLowerInvariant() switch
        {
            "type" => normalizedKind is "class" or "struct" or "enum" or "interface",
            "member" => normalizedKind is "member" or "function",
            _ => normalizedKind.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0,
        };
    }
}
