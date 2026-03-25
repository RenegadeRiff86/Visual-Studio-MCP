using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("VsIdeBridge")]

namespace VsIdeBridge.Diagnostics;

internal static class DiagnosticsLibraryMarker
{
    internal const string Purpose = "Own reusable diagnostics projection and warning-shaping logic.";
}
