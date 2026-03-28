using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

internal static class VsOpenLaunchController
{
    private const string FlagFileName = "vs-open-enabled.flag";

    private static readonly string FlagFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VsIdeBridge",
        FlagFileName);

    public static bool IsEnabled => File.Exists(FlagFilePath);

    public static JsonObject Enable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FlagFilePath)!);
        File.WriteAllText(FlagFilePath, string.Empty);
        return BuildStatus();
    }

    public static JsonObject Disable()
    {
        if (File.Exists(FlagFilePath))
        {
            File.Delete(FlagFilePath);
        }

        return BuildStatus();
    }

    public static JsonObject GetStatus() => BuildStatus();

    private static JsonObject BuildStatus() => new()
    {
        ["enabled"] = IsEnabled,
        ["flagPath"] = FlagFilePath,
    };
}
