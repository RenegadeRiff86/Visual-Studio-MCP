using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
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
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        string resolvedPath = ResolveDocumentPath(dte, filePath);

        ProjectItem? projectItem = null;
        try { projectItem = dte.Solution.FindProjectItem(resolvedPath); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

        if (projectItem is null)
        {
            return new JObject
            {
                [ResolvedPathProperty] = resolvedPath,
                ["count"] = 0,
                ["symbols"] = new JArray(),
                ["note"] = "File is not part of any project or code model is unavailable.",
            };
        }

        JArray symbols = new();
        string? note = TryCollectFileCodeElements(projectItem, symbols, maxDepth, kindFilter);

        JObject outlineResult = new()
        {
            [ResolvedPathProperty] = resolvedPath,
            ["count"] = symbols.Count,
            ["symbols"] = symbols,
        };
        if (note is not null) outlineResult["note"] = note;
        return outlineResult;
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
                    try { CollectOutlineSymbols(element, symbols, 0, maxDepth, kindFilter); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
                }

                return null;
            }

            return "No code model available for this file type.";
        }
        catch (Exception ex)
        {
            return $"Code model unavailable: {ex.Message}";
        }
    }

    private static void CollectOutlineSymbols(CodeElement element, JArray symbols, int depth, int maxDepth, string? kindFilter = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (depth > maxDepth) return;

        vsCMElement kind;
        try { kind = element.Kind; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); return; }

        string name = string.Empty;
        int startLine = 0, endLine = 0;
        try { name = element.Name ?? string.Empty; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        try { startLine = element.StartPoint?.Line ?? 0; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        try { endLine = element.EndPoint?.Line ?? 0; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

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
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

        if (children is null) return;
        foreach (CodeElement child in children)
        {
            try { CollectOutlineSymbols(child, symbols, depth + 1, maxDepth, kindFilter); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
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
