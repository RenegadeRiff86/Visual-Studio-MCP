using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;
#nullable enable

namespace VsIdeBridge.Commands;

internal static partial class SolutionProjectCommands
{
    // ── search-solutions ──────────────────────────────────────────────────────

    internal sealed class IdeSearchSolutionsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0244)
    {
        protected override string CanonicalName => "Tools.IdeSearchSolutions";

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            string rootPath = args.GetString("path") ?? GetDefaultSearchRoot();
            string? query = args.GetString("query");
            int maxDepth = args.GetInt32("max-depth", 6);
            int maxResults = args.GetInt32("max", 200);

            if (!Directory.Exists(rootPath))
                throw new CommandErrorException("path_not_found", $"Search root not found: {rootPath}");

            List<string> matches = FindSolutions(rootPath, query, maxDepth, maxResults);
            JObject[] results =
            [..
                matches.Select(f => new JObject
                {
                    ["name"] = Path.GetFileNameWithoutExtension(f),
                    ["fileName"] = Path.GetFileName(f),
                    ["path"] = f,
                    ["directory"] = Path.GetDirectoryName(f),
                    ["lastModified"] = File.GetLastWriteTime(f).ToString("O"),
                })
            ];

            return Task.FromResult(new CommandExecutionResult(
                $"Found {results.Length} solution(s) under '{rootPath}'.",
                new JObject { ["count"] = results.Length, ["root"] = rootPath, ["solutions"] = new JArray(results) }));
        }

        private static string GetDefaultSearchRoot()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string reposPath = Path.Combine(userProfile, "source", "repos");
            return Directory.Exists(reposPath) ? reposPath : userProfile;
        }

        private static List<string> FindSolutions(string root, string? query, int maxDepth, int maxResults)
        {
            List<string> results = [];
            SearchDirectory(root, query, maxDepth, 0, results, maxResults);
            results.Sort((a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
            return results;
        }

        private static void SearchDirectory(string dir, string? query, int maxDepth, int depth, List<string> results, int maxResults)
        {
            if (depth > maxDepth || results.Count >= maxResults) return;
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.sln").Concat(Directory.EnumerateFiles(dir, "*.slnx")))
                {
                    if (results.Count >= maxResults) break;
                    if (query is null || Path.GetFileNameWithoutExtension(file).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        results.Add(file);
                }
                foreach (string subDir in Directory.EnumerateDirectories(dir))
                {
                    if (results.Count >= maxResults) break;
                    string name = Path.GetFileName(subDir);
                    if (name.StartsWith(".") || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                        continue;
                    SearchDirectory(subDir, query, maxDepth, depth + 1, results, maxResults);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"Skipping solution search directory '{dir}': {ex.Message}");
            }
            catch (DirectoryNotFoundException ex)
            {
                Debug.WriteLine($"Skipping missing solution search directory '{dir}': {ex.Message}");
            }
        }
    }
}
