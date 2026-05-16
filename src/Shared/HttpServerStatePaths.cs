using System;
using System.IO;

namespace VsIdeBridge.Shared;

public static class HttpServerStatePaths
{
    private const string ProductDirectoryName = "VsIdeBridge";
    private const string SharedStateDirectoryName = "state";
    private const string HttpEnabledFlagFileName = "http-enabled.flag";

    public static string GetSharedStateDirectory()
    {
        string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(commonAppData))
        {
            return Path.Combine(commonAppData, ProductDirectoryName, SharedStateDirectoryName);
        }

        return Path.Combine(Path.GetTempPath(), ProductDirectoryName, SharedStateDirectoryName);
    }

    public static string GetHttpEnabledFlagPath()
    {
        return Path.Combine(GetSharedStateDirectory(), HttpEnabledFlagFileName);
    }

    private const string StreamableHttpEnabledFlagFileName = "streamable-http-enabled.flag";

    public static string GetStreamableHttpEnabledFlagPath()
    {
        return Path.Combine(GetSharedStateDirectory(), StreamableHttpEnabledFlagFileName);
    }

    private const string BestPracticeDisabledFlagFileName = "best-practice-disabled.flag";

    public static string GetBestPracticeDisabledFlagPath()
    {
        return Path.Combine(GetSharedStateDirectory(), BestPracticeDisabledFlagFileName);
    }
}
