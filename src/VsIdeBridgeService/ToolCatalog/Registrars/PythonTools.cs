using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridgeService.SystemTools;
using IOPath = System.IO.Path;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private static IEnumerable<ToolEntry> PythonNativeTools() =>
        PythonDiscoveryTools()
            .Concat(PythonMutationTools())
            .Concat(CondaTools());

    private static IEnumerable<ToolEntry> PythonDiscoveryTools()
    {
        yield return new("python_eval",
            "Evaluate a single Python expression in a stateless scratchpad for math and quick checks.",
            ObjectSchema(
                Req("expression", "Python expression to evaluate, for example \"math.sqrt(2)\"."),
                Opt(Path, "Optional interpreter path. Defaults to the active interpreter, managed runtime, or first discovered Python.")),
            Python,
            async (id, args, bridge) =>
            {
                string expression = RequireArg(id, args, "expression");
                string python = ResolvePythonInterpreterPath(id, args);
                return await RunPythonEvalAsync(id, python, expression, timeoutMs: 5_000)
                    .ConfigureAwait(false);
            });

        yield return new("python_exec",
            "Execute a short stateless Python snippet for calculations and quick data transforms.",
            ObjectSchema(
                Req("code", "Short Python snippet. If you assign to a variable named result, it will be returned separately."),
                Opt(Path, "Optional interpreter path. Defaults to the active interpreter, managed runtime, or first discovered Python.")),
            Python,
            async (id, args, bridge) =>
            {
                string code = RequireArg(id, args, "code");
                string python = ResolvePythonInterpreterPath(id, args);
                return await RunPythonExecAsync(id, python, code, timeoutMs: 10_000)
                    .ConfigureAwait(false);
            });

        yield return new("python_list_envs",
            "Enumerate available Python interpreters: PATH entries, common venv locations under " +
            "the solution directory, and the bridge-managed Python runtime if present.",
            EmptySchema(), Python,
            async (id, _, bridge) =>
            {
                string solutionDir = ServiceToolPaths.ResolveSolutionDirectory(bridge);
                List<string> found = FindPythonInterpreters(solutionDir);

                JsonArray arr = new();
                foreach (string p in found)
                    arr.Add(JsonValue.Create(p));

                JsonObject payload = new() { ["interpreters"] = arr, ["count"] = found.Count };
                return MakePythonResult(payload, success: true);
            });

        yield return new("python_env_info",
            "Return version, executable path, and site-packages directory for a Python interpreter.",
            ObjectSchema(Req(Path, "Absolute path to the python.exe / python3 interpreter.")),
            Python,
            async (id, args, bridge) =>
            {
                string python = RequireArg(id, args, Path);
                string script =
                    "import sys, json; " +
                    "import site; " +
                    "print(json.dumps({" +
                    "'executable': sys.executable," +
                    "'version': sys.version," +
                    "'version_info': list(sys.version_info[:3])," +
                    "'site_packages': site.getsitepackages() if hasattr(site,'getsitepackages') else []" +
                    "}))";
                return await RunPythonScriptAsync(id, python, script, timeoutMs: 15_000)
                    .ConfigureAwait(false);
            });

        yield return new("python_list_packages",
            "List installed packages in a Python environment using pip list --format=json.",
            ObjectSchema(Req(Path, "Absolute path to the python.exe / python3 interpreter.")),
            Python,
            async (id, args, bridge) =>
            {
                string python = RequireArg(id, args, Path);
                return await RunPythonModuleAsync(
                    id, python, "pip list --format=json", timeoutMs: 30_000)
                    .ConfigureAwait(false);
            });
    }

    private static IEnumerable<ToolEntry> PythonMutationTools()
    {
        const string PackagesArgName = "packages";

        yield return new("python_install_package",
            "Install one or more packages into a Python environment using pip.",
            ObjectSchema(
                Req(Path, "Absolute path to the python.exe / python3 interpreter."),
                Req(PackagesArgName, "Space-separated package specifiers, e.g. \"requests>=2.28 flask\".")),
            Python,
            async (id, args, bridge) =>
            {
                string python = RequireArg(id, args, Path);
                string packages = RequireArg(id, args, PackagesArgName);
                return await RunPythonModuleAsync(
                    id, python, $"pip install {packages}", timeoutMs: 120_000)
                    .ConfigureAwait(false);
            });

        yield return new("python_remove_package",
            "Uninstall one or more packages from a Python environment using pip.",
            ObjectSchema(
                Req(Path, "Absolute path to the python.exe / python3 interpreter."),
                Req(PackagesArgName, "Space-separated package names to remove.")),
            Python,
            async (id, args, bridge) =>
            {
                string python = RequireArg(id, args, Path);
                string packages = RequireArg(id, args, PackagesArgName);
                return await RunPythonModuleAsync(
                    id, python, $"pip uninstall -y {packages}", timeoutMs: 60_000)
                    .ConfigureAwait(false);
            });

        yield return new("python_create_env",
            "Create a new virtual environment at the specified path using python -m venv.",
            ObjectSchema(
                Req(Path, "Absolute path for the new virtual environment directory."),
                Opt("base_python", "Optional: path to the base interpreter to use (defaults to system python).")),
            Python,
            async (id, args, bridge) =>
            {
                string targetPath = RequireArg(id, args, Path);
                string? basePython = args?["base_python"]?.GetValue<string>();
                string python = string.IsNullOrWhiteSpace(basePython)
                    ? FindSystemPython()
                    : basePython;
                return await RunPythonModuleAsync(
                    id, python, $"venv \"{targetPath}\"", timeoutMs: 60_000)
                    .ConfigureAwait(false);
            });
    }

    private static IEnumerable<ToolEntry> CondaTools()
    {
        yield return new("conda_install",
            "Install packages into a conda environment.",
            ObjectSchema(
                Req("packages", "Space-separated package specifiers, e.g. \"numpy scipy\"."),
                Opt("env", "Optional: conda environment name or path. Defaults to base/active env.")),
            Python,
            async (id, args, bridge) =>
            {
                string conda = ResolveCondaExecutable(id);
                string packages = RequireArg(id, args, "packages");
                string? env = args?["env"]?.GetValue<string>();
                string envArg = string.IsNullOrWhiteSpace(env) ? string.Empty
                    : env.Contains(IOPath.DirectorySeparatorChar) || env.Contains('/')
                        ? $"-p \"{env}\""
                        : $"-n \"{env}\"";
                return await RunSubprocessAsync(
                    id, conda, $"install -y {envArg} {packages}".TrimEnd(),
                    workingDirectory: null, timeoutMs: 120_000)
                    .ConfigureAwait(false);
            });

        yield return new("conda_remove",
            "Remove packages from a conda environment.",
            ObjectSchema(
                Req("packages", "Space-separated package names to remove."),
                Opt("env", "Optional: conda environment name or path.")),
            Python,
            async (id, args, bridge) =>
            {
                string conda = ResolveCondaExecutable(id);
                string packages = RequireArg(id, args, "packages");
                string? env = args?["env"]?.GetValue<string>();
                string envArg = string.IsNullOrWhiteSpace(env) ? string.Empty
                    : env.Contains(IOPath.DirectorySeparatorChar) || env.Contains('/')
                        ? $"-p \"{env}\""
                        : $"-n \"{env}\"";
                return await RunSubprocessAsync(
                    id, conda, $"remove -y {envArg} {packages}".TrimEnd(),
                    workingDirectory: null, timeoutMs: 60_000)
                    .ConfigureAwait(false);
            });
    }

    // ── Python helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Run a Python -c script and return a structured result.</summary>
    private static async Task<JsonNode> RunPythonScriptAsync(
        JsonNode? id, string python, string script, int timeoutMs)
    {
        return await RunSubprocessAsync(id, python, $"-c \"{script.Replace("\"", "\\\"")}\"",
            workingDirectory: null, timeoutMs).ConfigureAwait(false);
    }

    private static async Task<JsonNode> RunPythonEvalAsync(
        JsonNode? id, string python, string expression, int timeoutMs)
    {
        string script =
            "import json, math, statistics, decimal, fractions, base64, sys; " +
            "expr = base64.b64decode(sys.stdin.read()).decode('utf-8'); " +
            "safe = {'abs': abs, 'min': min, 'max': max, 'sum': sum, 'round': round, 'len': len}; " +
            "globals_dict = {'__builtins__': safe, 'math': math, 'statistics': statistics, 'decimal': decimal, 'fractions': fractions}; " +
            "value = eval(expr, globals_dict, {}); " +
            "payload = {'expression': expr, 'type': type(value).__name__, 'repr': repr(value)}; " +
            "json.dumps(value); payload['json'] = value; " +
            "print(json.dumps(payload))";

        string encodedExpression = Convert.ToBase64String(Encoding.UTF8.GetBytes(expression));
        return await RunSubprocessAsync(id, python, $"-c \"{script.Replace("\"", "\\\"")}\"",
            workingDirectory: null, timeoutMs, encodedExpression).ConfigureAwait(false);
    }

    private static async Task<JsonNode> RunPythonExecAsync(
        JsonNode? id, string python, string code, int timeoutMs)
    {
        string script =
            "import json, math, statistics, decimal, fractions, base64, io, contextlib, sys\n" +
            "source = base64.b64decode(sys.stdin.read()).decode('utf-8')\n" +
            "safe = {'abs': abs, 'min': min, 'max': max, 'sum': sum, 'round': round, 'len': len, 'range': range, 'print': print}\n" +
            "globals_dict = {'__builtins__': safe, 'math': math, 'statistics': statistics, 'decimal': decimal, 'fractions': fractions}\n" +
            "locals_dict = {}\n" +
            "stdout_buffer = io.StringIO()\n" +
            "with contextlib.redirect_stdout(stdout_buffer):\n" +
            "    exec(source, globals_dict, locals_dict)\n" +
            "payload = {'stdout': stdout_buffer.getvalue(), 'hasResult': 'result' in locals_dict}\n" +
            "if 'result' in locals_dict:\n" +
            "    value = locals_dict['result']\n" +
            "    payload['resultType'] = type(value).__name__\n" +
            "    payload['resultRepr'] = repr(value)\n" +
            "    json.dumps(value)\n" +
            "    payload['resultJson'] = value\n" +
            "print(json.dumps(payload))";

        string encodedCode = Convert.ToBase64String(Encoding.UTF8.GetBytes(code));
        return await RunSubprocessAsync(id, python, $"-c \"{script.Replace("\"", "\\\"")}\"",
            workingDirectory: null, timeoutMs, encodedCode).ConfigureAwait(false);
    }

    /// <summary>Run a python -m module command (e.g. pip install ...).</summary>
    private static async Task<JsonNode> RunPythonModuleAsync(
        JsonNode? id, string python, string moduleArgs, int timeoutMs)
    {
        return await RunSubprocessAsync(id, python, $"-m {moduleArgs}",
            workingDirectory: null, timeoutMs).ConfigureAwait(false);
    }

    /// <summary>General-purpose subprocess runner for python/conda tools.</summary>
    private static async Task<JsonNode> RunSubprocessAsync(
        JsonNode? id, string exe, string args, string? workingDirectory, int timeoutMs, string? standardInput = null)
    {
        ProcessStartInfo psi = new()
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(psi)
            ?? throw new McpRequestException(id, McpErrorCodes.BridgeError,
                $"Failed to start process '{exe}'.");

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Task waitTask = process.WaitForExitAsync();

        if (!ReferenceEquals(
                await Task.WhenAny(waitTask, Task.Delay(timeoutMs)).ConfigureAwait(false),
                waitTask))
        {
            TryKillProcess(process);
            throw new McpRequestException(id, McpErrorCodes.TimeoutError,
                $"'{exe} {args}' timed out after {timeoutMs} ms.");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        bool success = process.ExitCode == 0;

        JsonObject payload = new()
        {
            ["success"] = success,
            ["exitCode"] = process.ExitCode,
            ["stdout"] = stdout,
            ["stderr"] = stderr,
        };

        return MakePythonResult(payload, success);
    }

    private static string ResolvePythonInterpreterPath(JsonNode? id, JsonObject? args)
    {
        string? explicitPath = args?[Path]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        string? activeInterpreterPath = PythonInterpreterState.LoadActiveInterpreterPath();
        if (!string.IsNullOrWhiteSpace(activeInterpreterPath) && File.Exists(activeInterpreterPath))
        {
            return activeInterpreterPath;
        }

        string managedRuntimePath = IOPath.GetFullPath(
            IOPath.Combine(AppContext.BaseDirectory, "..", "python", "managed-runtime", "python.exe"));
        if (File.Exists(managedRuntimePath))
        {
            return managedRuntimePath;
        }

        string fallbackPython = FindSystemPython();
        if (File.Exists(fallbackPython))
        {
            return fallbackPython;
        }

        throw new McpRequestException(id, McpErrorCodes.BridgeError,
            "Python interpreter not found. Pass 'path' explicitly or install the managed Python runtime.");
    }

    private static JsonNode MakePythonResult(JsonObject payload, bool success)
        => ToolResultFormatter.StructuredToolResult(
            payload,
            isError: !success,
            successText: $"Python command completed with exit code {payload["exitCode"]?.GetValue<int>() ?? 0}.");

    private static string RequireArg(JsonNode? id, JsonObject? args, string name)
    {
        string? value = args?[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
            throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                $"Missing required argument '{name}'.");
        return value;
    }

    // ── Python interpreter discovery ────────────────────────────────────────────────

    private static List<string> FindPythonInterpreters(string solutionDir)
    {
        List<string> results = new();

        // 1. PATH scan
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string dir in pathEnv.Split(IOPath.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (string name in new[] { "python.exe", "python3.exe", "python" })
                {
                    string candidate = IOPath.Combine(dir, name);
                    if (File.Exists(candidate) && !results.Contains(candidate))
                        results.Add(candidate);
                }
            }
        }

        // 2. Common venv locations inside the solution directory
        foreach (string rel in new[] { ".venv", "venv", "env", ".env" })
        {
            foreach (string name in new[] { "python.exe", "python3.exe" })
            {
                string candidate = IOPath.Combine(solutionDir, rel, "Scripts", name);
                if (File.Exists(candidate) && !results.Contains(candidate))
                    results.Add(candidate);

                // Linux/macOS style (bin/)
                candidate = IOPath.Combine(solutionDir, rel, "bin", name);
                if (File.Exists(candidate) && !results.Contains(candidate))
                    results.Add(candidate);
            }
        }

        // 3. Bridge-managed Python (AppData\Local\Programs\Python)
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (string root in new[] { IOPath.Combine(localAppData, "Programs", "Python"),
                                        programFiles })
        {
            if (!Directory.Exists(root)) continue;
            foreach (string dir in Directory.EnumerateDirectories(root, "Python*",
                SearchOption.TopDirectoryOnly))
            {
                string candidate = IOPath.Combine(dir, "python.exe");
                if (File.Exists(candidate) && !results.Contains(candidate))
                    results.Add(candidate);
            }
        }

        return results;
    }

    private static string FindSystemPython()
    {
        foreach (string name in new[] { "python.exe", "python3.exe", "python", "python3" })
        {
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathEnv)) continue;
            foreach (string dir in pathEnv.Split(IOPath.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string candidate = IOPath.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        throw new InvalidOperationException(
            "Python interpreter not found on PATH. Pass base_python explicitly.");
    }

    // ── Conda resolution ────────────────────────────────────────────────────────────

    private static string? _resolvedConda;

    private static string ResolveCondaExecutable(JsonNode? id)
    {
        if (_resolvedConda is not null) return _resolvedConda;

        // 1. CONDA_EXE env var (set by conda activate)
        string? fromEnv = Environment.GetEnvironmentVariable("CONDA_EXE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            if (File.Exists(fromEnv)) return _resolvedConda = fromEnv;
            throw new McpRequestException(id, McpErrorCodes.InvalidParams,
                $"CONDA_EXE points to '{fromEnv}' but the file does not exist.");
        }

        // 2. PATH scan
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string dir in pathEnv.Split(IOPath.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (string name in new[] { "conda.exe", "conda.bat", "conda" })
                {
                    string candidate = IOPath.Combine(dir, name);
                    if (File.Exists(candidate)) return _resolvedConda = candidate;
                }
            }
        }

        // 3. Known install locations (Miniconda/Anaconda under user profile + LocalAppData)
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string root in new[] { userProfile, localAppData })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (string rel in new[]
            {
                @"miniconda3\Scripts\conda.exe",
                @"miniconda3\condabin\conda.bat",
                @"anaconda3\Scripts\conda.exe",
                @"anaconda3\condabin\conda.bat",
                @"Miniconda3\Scripts\conda.exe",
                @"Anaconda3\Scripts\conda.exe",
            })
            {
                string candidate = IOPath.Combine(root, rel);
                if (File.Exists(candidate)) return _resolvedConda = candidate;
            }
        }

        throw new McpRequestException(id, McpErrorCodes.BridgeError,
            "Conda executable not found. Install Miniconda/Anaconda or set CONDA_EXE.");
    }
}
