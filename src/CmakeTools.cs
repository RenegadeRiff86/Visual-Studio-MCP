using System.Text.Json.Nodes;
using VsIdeBridgeService.SystemTools;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string CmakeCategory = "project";

    private static IEnumerable<ToolEntry> CmakeTools()
    {
        yield return CreateCmakeConfigureTool();
        yield return CreateCmakeCacheTool();
    }

    private static ToolEntry CreateCmakeConfigureTool()
        => new("cmake_configure",
            "Run cmake to configure or re-configure a CMake project, regenerating Visual Studio " +
            "project files (vcxproj / slnx). Use this after editing CMakeLists.txt or " +
            "CMakePresets.json when VS has not automatically re-generated the project. " +
            "Pass preset to use a named preset from CMakePresets.json / CMakeUserPresets.json " +
            "(runs 'cmake --preset <name>' from source_dir). " +
            "Omit preset and supply build_dir for manual configuration " +
            "('cmake -S <source_dir> -B <build_dir>'). " +
            "source_dir defaults to the git repository root of the bound solution.",
            ObjectSchema(
                Opt("preset",
                    "CMake preset name defined in CMakePresets.json or CMakeUserPresets.json. " +
                    "When provided, runs 'cmake --preset <name>' from source_dir."),
                Opt("source_dir",
                    "Source directory containing the top-level CMakeLists.txt. " +
                    "Defaults to the git repository root of the bound solution."),
                Opt("build_dir",
                    "Build output directory. Required when preset is omitted. " +
                    "Ignored when preset is provided (the preset defines binaryDir)."),
                OptArr("extra_args",
                    "Additional cmake arguments such as '-DFOO=BAR' or '--fresh'. " +
                    "Each element is passed as a separate argument."),
                OptInt("timeout_seconds",
                    "Override the default 180-second timeout for very large projects.")),
            CmakeCategory,
            async (id, args, bridge) =>
            {
                string sourceDir = OptionalString(args, "source_dir")
                    ?? ServiceToolPaths.ResolveRepoRootDirectory(bridge);
                string? preset   = OptionalString(args, "preset");
                string? buildDir = OptionalString(args, "build_dir");
                int timeoutMs    = ((args?["timeout_seconds"]?.GetValue<int?>()) ?? 180) * 1000;

                List<string> cmakeArgs = BuildCmakeConfigureArgs(id, sourceDir, preset, buildDir);

                if (args?["extra_args"] is JsonArray extraArr)
                {
                    foreach (JsonNode? node in extraArr)
                    {
                        string? extra = node?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(extra))
                            cmakeArgs.Add(extra);
                    }
                }

                return await CmakeRunner.RunAsync(id, sourceDir, cmakeArgs, timeoutMs)
                    .ConfigureAwait(false);
            },
            searchHints: BuildSearchHints(
                workflow:
                [
                    ("build_solution",  "Build the solution after reconfiguring"),
                    ("errors",          "Check for build errors after the build"),
                    ("open_solution",   "Reload the solution if VS doesn't pick up new projects automatically"),
                ],
                related:
                [
                    ("cmake_cache",  "Inspect CMake cache variables for the build directory"),
                    ("git_status",   "Review CMakeLists.txt changes before reconfiguring"),
                ]));

    private static List<string> BuildCmakeConfigureArgs(
        JsonNode? id, string sourceDir, string? preset, string? buildDir)
    {
        if (!string.IsNullOrWhiteSpace(preset))
            return ["--preset", preset];

        if (!string.IsNullOrWhiteSpace(buildDir))
            return ["-S", sourceDir, "-B", buildDir];

        throw new McpRequestException(id, McpErrorCodes.InvalidParams,
            "cmake_configure requires either 'preset' or 'build_dir'. " +
            "Provide a CMake preset name from CMakePresets.json, or an explicit build directory path.");
    }

    private static ToolEntry CreateCmakeCacheTool()
        => new("cmake_cache",
            "List the non-advanced CMake cache variables for a build directory. " +
            "Equivalent to 'cmake -L -N -B <build_dir>'. " +
            "Use this to inspect configured options or verify that a configure step applied correctly.",
            ObjectSchema(
                Req("build_dir",
                    "Path to the CMake build directory (the directory that contains CMakeCache.txt)."),
                Opt("filter",
                    "Optional substring filter applied to variable names (case-insensitive). " +
                    "Only variables whose names contain the filter string are returned.")),
            CmakeCategory,
            async (id, args, bridge) =>
            {
                string buildDir = RequiredString(id, args, "build_dir");
                string? filter  = OptionalString(args, "filter");

                JsonNode result = await CmakeRunner.RunAsync(id, buildDir,
                    ["-L", "-N", "-B", buildDir])
                    .ConfigureAwait(false);

                ApplyCmakeCacheFilter(result, filter);
                return result;
            },
            searchHints: BuildSearchHints(
                workflow: [("cmake_configure", "Re-run cmake configure")],
                related: [("build_solution", "Build the configured project")]));

    private static void ApplyCmakeCacheFilter(JsonNode result, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return;

        if (result is not JsonObject obj || obj["stdout"]?.GetValue<string>() is not { } raw)
            return;

        obj["stdout"] = string.Join(
            Environment.NewLine,
            raw.Split('\n')
               .Where(line =>
                   line.StartsWith("//") ||
                   line.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }
}
