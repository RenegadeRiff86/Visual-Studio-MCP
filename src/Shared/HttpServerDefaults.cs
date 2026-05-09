namespace VsIdeBridge.Shared;

public static class HttpServerDefaults
{
    public const int HttpPort = 43117;
    public const string HttpPortText = "43117";
    public const int StreamableHttpPort = 43118;
    public const string StreamableHttpPortText = "43118";

    public static string HttpUrl => $"http://localhost:{HttpPort}/";
    public static string StreamableHttpUrl => $"http://localhost:{StreamableHttpPort}/mcp";
}
