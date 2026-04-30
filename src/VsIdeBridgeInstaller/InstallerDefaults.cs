namespace VsIdeBridgeInstaller;

internal static class InstallerDefaults
{
    internal const string DefaultConfiguration = "Release";
    internal const string ProductName = "VS IDE Bridge";
    internal const string InstallFolderName = "VsIdeBridge";
    internal const string SolutionFileName = "VsIdeBridge.sln";
    internal const string ServiceDirectoryName = "service";
    internal const string VsixDirectoryName = "vsix";
    internal const string PythonDirectoryName = "python";
    internal const string ManagedRuntimeDirectoryName = "managed-runtime";
    internal const string ServiceExecutableName = "VsIdeBridgeService.exe";
    internal const string LauncherExecutableName = "VsIdeBridgeLauncher.exe";
    internal const string VsixFileName = "VsIdeBridge.vsix";
    internal const string InstallerExecutableName = "vs-ide-bridge-installer";
    internal const string ServiceName = "VsIdeBridgeService";
    internal const string ServiceDisplayName = "VS IDE Bridge Service";
    internal const string ServiceDescription = "VS IDE Bridge background host (automatic start, idle auto-stop).";
    internal const string VsixId = "RenegadeRiff86.VsIdeBridge";
    internal const string LegacyVsixId = "StanElston.VsIdeBridge";
    internal const string VisualStudioRootFolderName = "Microsoft Visual Studio";
    internal const string VisualStudioInstallerDirectoryName = "Installer";
    internal const string VswhereExecutableName = "vswhere.exe";
    internal const string VisualStudioCoreEditorComponentId = "Microsoft.VisualStudio.Component.CoreEditor";
    internal const string VsixInstallerRelativePath = "Common7\\IDE\\VSIXInstaller.exe";
    internal const int DefaultIdleSoftSeconds = 900;
    internal const int DefaultIdleHardSeconds = 1200;

    internal static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        InstallFolderName);

    internal static string GetManagedRuntimePath(string installDir)
        => Path.Combine(installDir, PythonDirectoryName, ManagedRuntimeDirectoryName, "python.exe");
}
