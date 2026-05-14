using System;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed partial class DocumentService(IServiceProvider serviceProvider)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    /// <summary>Injected after construction; resolves handle IDs in path arguments.</summary>
    internal HandleService? HandleService { get; set; }
    private const string DefinitionFoundProperty = "definitionFound";
    private const string DefinitionLocationProperty = "definitionLocation";
    private const string DefinitionStatusProperty = "definitionStatus";
    private const string DocumentNotFoundCode = "document_not_found";
    private const string ImplementationFoundProperty = "implementationFound";
    private const string ImplementationLocationProperty = "implementationLocation";
    private const string ResolvedPathProperty = "resolvedPath";
    private const int RecommendedMaxOpenTabs = 7;
    private const string SelectedTextProperty = "selectedText";
    private const string SourceLocationProperty = "sourceLocation";
    private const string TabManagementNote =
        "Prefer keeping about 7 tabs open. Use close_others, close_document, or close_file when tab count grows.";
    private const string TextDocumentKind = "TextDocument";
    private const string UnsupportedOperationCode = "unsupported_operation";
}
