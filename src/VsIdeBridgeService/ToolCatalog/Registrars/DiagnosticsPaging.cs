using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using VsIdeBridge.Diagnostics;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private static string? GetBridgeDiagnosticsMax(JsonObject? args)
        => WantsFullDiagnosticsPayload(args) ? OptionalText(args, Max) : null;

    private static DiagnosticQueryOptions CreateDiagnosticQueryOptions(JsonObject? args)
        => DiagnosticQueryOptions.FromJsonObject(args, DefaultCompactDiagnosticsRows);
}
