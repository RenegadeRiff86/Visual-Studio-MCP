using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed partial class DocumentService
{
    public async Task<JObject> ListOpenTabsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        string? activePath = TryGetDocumentFullName(dte.ActiveDocument);
        IReadOnlyList<Document> documents = EnumerateOpenDocuments(dte);
        JArray items = new();
        for (int i = 0; i < documents.Count; i++)
        {
            items.Add(CreateDocumentInfo(documents[i], activePath, i + 1));
        }

        return new JObject
        {
            ["count"] = items.Count,
            ["activePath"] = string.IsNullOrWhiteSpace(activePath) ? string.Empty : PathNormalization.NormalizeFilePath(activePath),
            ["recommendedMaxOpenTabs"] = RecommendedMaxOpenTabs,
            ["isOverRecommendedTabCount"] = items.Count > RecommendedMaxOpenTabs,
            ["note"] = TabManagementNote,
            ["items"] = items,
        };
    }

    public async Task<JObject> ListOpenDocumentsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        string? activePath = TryGetDocumentFullName(dte.ActiveDocument);
        JArray items = new(
            EnumerateOpenDocuments(dte)
                .Select((document, index) => CreateDocumentInfo(document, activePath, index + 1)));

        return new JObject
        {
            ["count"] = items.Count,
            ["items"] = items,
        };
    }

    private static IReadOnlyList<Document> EnumerateOpenDocuments(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return
        [
            .. dte.Documents
                .Cast<Document>()
                .Where(HasDocumentPath),
        ];
    }
}
