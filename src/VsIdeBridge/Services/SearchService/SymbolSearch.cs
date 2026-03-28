using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed partial class SearchService
{
    private async Task<(string Path, string ProjectUniqueName)> GetDocumentTargetAsync(IdeCommandContext context, string? pathFilter = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        (string Path, string ProjectUniqueName) explicitTarget = TryResolveExplicitDocumentTarget(context.Dte, pathFilter);
        if (!string.IsNullOrWhiteSpace(explicitTarget.Path))
        {
            return explicitTarget;
        }

        Document? activeDocument = context.Dte.ActiveDocument;
        if (activeDocument is null || string.IsNullOrWhiteSpace(activeDocument.FullName))
        {
            throw new CommandErrorException("document_not_found", "There is no active document.");
        }

        return (
            PathNormalization.NormalizeFilePath(activeDocument.FullName),
            activeDocument.ProjectItem?.ContainingProject?.UniqueName ?? string.Empty);
    }

    private static IEnumerable<CodeModelHit> SearchCodeModelSymbols(
        DTE2 dte,
        string query,
        string kind,
        string scope,
        bool matchCase,
        string? projectUniqueName,
        string? pathFilter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string? normalizedPathFilter = NormalizeSearchPathFilter(dte, pathFilter);
        (string Path, string ProjectUniqueName) activeDocument = TryResolveExplicitDocumentTarget(dte, normalizedPathFilter);
        if (string.IsNullOrWhiteSpace(activeDocument.Path))
        {
            activeDocument = TryGetActiveDocumentTarget(dte);
        }

        IEnumerable<(string Path, string ProjectUniqueName)> files = scope switch
        {
            "document" => string.IsNullOrWhiteSpace(activeDocument.Path)
                ? []
                : new[] { activeDocument },
            "open" => EnumerateOpenFiles(dte),
            "project" => EnumerateSolutionFiles(dte)
                .Where(item => string.Equals(item.ProjectUniqueName, projectUniqueName, StringComparison.OrdinalIgnoreCase)),
            _ => EnumerateSolutionFiles(dte),
        };

        if (!string.IsNullOrWhiteSpace(normalizedPathFilter))
        {
            files = files.Where(item => MatchesPathFilter(item.Path, normalizedPathFilter));
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<CodeModelHit> hits = [];
        foreach ((string Path, string ProjectUniqueName) file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                continue;
            }

            ProjectItem? projectItem = null;
            CodeElements? elements = null;
            try
            {
                projectItem = dte.Solution.FindProjectItem(file.Path);
                elements = projectItem?.FileCodeModel?.CodeElements;
            }
            catch (Exception ex)
            {
                TraceSearchFailure("FindProjectItem", ex);
            }

            if (projectItem is null || elements is null)
            {
                continue;
            }

            foreach (CodeElement element in elements)
            {
                CollectMatchingSymbols(element, file.Path, file.ProjectUniqueName, query, kind, comparison, hits, seen);
            }
        }

        return hits
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(hit => hit.Line)
            .ThenBy(hit => hit.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static void CollectMatchingSymbols(
        CodeElement element,
        string path,
        string projectUniqueName,
        string query,
        string kindFilter,
        StringComparison comparison,
        List<CodeModelHit> hits,
        HashSet<string> seen)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        vsCMElement kind;
        try
        {
            kind = element.Kind;
        }
        catch (Exception ex)
        {
            TraceSearchFailure("CollectMatchingSymbols.Kind", ex);
            return;
        }

        if (!s_codeModelKinds.Contains(kind))
        {
            return;
        }

        if (!TryCreateMatchingSymbolHit(element, kind, path, projectUniqueName, query, kindFilter, comparison, out CodeModelHit? hit, out string key))
        {
            return;
        }

        if (hit is not null && seen.Add(key))
        {
            hits.Add(hit);
        }

        foreach (CodeElement child in EnumerateChildren(element))
        {
            CollectMatchingSymbols(child, path, projectUniqueName, query, kindFilter, comparison, hits, seen);
        }
    }

    private static bool TryCreateMatchingSymbolHit(
        CodeElement element,
        vsCMElement kind,
        string path,
        string projectUniqueName,
        string query,
        string kindFilter,
        StringComparison comparison,
        out CodeModelHit? hit,
        out string key)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        hit = null;
        key = string.Empty;

        string normalizedKind = NormalizeKind(kind);
        if (!MatchesKind(kindFilter, normalizedKind))
        {
            return false;
        }

        string name = TryGetElementName(element);
        string fullName = TryGetFullName(element);
        int score = ScoreSymbolMatch(query, name, fullName, comparison, out string matchKind);
        if (score <= 0)
        {
            return false;
        }

        int line = TryGetLine(element.StartPoint);
        int endLine = TryGetLine(element.EndPoint);
        key = $"{path}|{normalizedKind}|{fullName}|{line}";
        hit = new CodeModelHit
        {
            Path = path,
            ProjectUniqueName = projectUniqueName,
            Name = name,
            FullName = fullName,
            Kind = normalizedKind,
            Signature = TryGetSignature(element, fullName, name),
            Line = line,
            EndLine = endLine,
            Score = score,
            MatchKind = matchKind,
        };
        return true;
    }

    private static IEnumerable<CodeElement> EnumerateChildren(CodeElement element)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        CodeElements? children = null;
        try
        {
            children = element switch
            {
                CodeNamespace codeNamespace => codeNamespace.Members,
                CodeClass codeClass => codeClass.Members,
                CodeStruct codeStruct => codeStruct.Members,
                CodeInterface codeInterface => codeInterface.Members,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            TraceSearchFailure("EnumerateChildren", ex);
        }

        if (children is null)
        {
            yield break;
        }

        foreach (CodeElement child in children)
        {
            yield return child;
        }
    }

    private static string NormalizeKind(vsCMElement kind)
    {
        return kind switch
        {
            vsCMElement.vsCMElementFunction => FunctionKind,
            vsCMElement.vsCMElementClass => "class",
            vsCMElement.vsCMElementStruct => "struct",
            vsCMElement.vsCMElementEnum => "enum",
            vsCMElement.vsCMElementNamespace => "namespace",
            vsCMElement.vsCMElementInterface => InterfaceKind,
            vsCMElement.vsCMElementProperty => "member",
            vsCMElement.vsCMElementVariable => "member",
            _ => "unknown",
        };
    }

    private static bool MatchesKind(string kindFilter, string normalizedKind)
    {
        return kindFilter.ToLowerInvariant() switch
        {
            "all" => true,
            "type" => normalizedKind is "class" or "struct" or "enum" or "interface",
            "member" => normalizedKind is "member" or "function",
            _ => string.Equals(kindFilter, normalizedKind, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string TryGetElementName(CodeElement element)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            return element.Name ?? string.Empty;
        }
        catch (Exception ex)
        {
            TraceSearchFailure("TryGetElementName", ex);
            return string.Empty;
        }
    }

    private static string TryGetFullName(CodeElement element)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            return string.IsNullOrWhiteSpace(element.FullName) ? TryGetElementName(element) : element.FullName;
        }
        catch (Exception ex)
        {
            TraceSearchFailure("TryGetFullName", ex);
            return TryGetElementName(element);
        }
    }

    private static string TryGetSignature(CodeElement element, string fullName, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (element is CodeFunction function)
            {
                return function.get_Prototype(
                    ((int)vsCMPrototype.vsCMPrototypeFullname)
                    | ((int)vsCMPrototype.vsCMPrototypeParamTypes)
                    | ((int)vsCMPrototype.vsCMPrototypeType))
                    ?? fullName;
            }
        }
        catch (Exception ex)
        {
            TraceSearchFailure("TryGetSignature", ex);
        }

        return string.IsNullOrWhiteSpace(fullName) ? name : fullName;
    }

    private static int TryGetLine(TextPoint? point)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            return point?.Line ?? 0;
        }
        catch (Exception ex)
        {
            TraceSearchFailure("TryGetLine", ex);
            return 0;
        }
    }

    private static int ScoreSymbolMatch(
        string query,
        string name,
        string fullName,
        StringComparison comparison,
        out string matchKind)
    {
        matchKind = string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        if (string.Equals(name, query, comparison))
        {
            matchKind = "name-exact";
            return 1000;
        }

        if (string.Equals(fullName, query, comparison))
        {
            matchKind = "full-name-exact";
            return 950;
        }

        if (name.StartsWith(query, comparison))
        {
            matchKind = "name-prefix";
            return 875;
        }

        if (fullName.StartsWith(query, comparison))
        {
            matchKind = "full-name-prefix";
            return 850;
        }

        if (name.IndexOf(query, comparison) >= 0)
        {
            matchKind = "name-contains";
            return 760;
        }

        if (fullName.IndexOf(query, comparison) >= 0)
        {
            matchKind = "full-name-contains";
            return 720;
        }

        return 0;
    }
}
