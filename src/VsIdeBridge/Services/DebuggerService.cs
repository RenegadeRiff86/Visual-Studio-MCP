using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Infrastructure;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace VsIdeBridge.Services;

internal sealed class DebuggerService
{
    public async Task<JObject> GetStateAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var debugger = dte.Debugger;
        var data = new JObject
        {
            ["mode"] = debugger.CurrentMode.ToString(),
            ["currentProcess"] = debugger.CurrentProcess?.Name ?? string.Empty,
            ["processes"] = GetDebuggedProcessNames(debugger),
            ["threads"] = GetThreadSummaries(debugger.CurrentProgram),
        };

        if (debugger.CurrentMode == dbgDebugMode.dbgBreakMode && debugger.CurrentStackFrame is StackFrame frame)
        {
            data["currentStackFrame"] = new JObject
            {
                ["function"] = frame.FunctionName ?? string.Empty,
                ["language"] = frame.Language ?? string.Empty,
            };

            if (TryGetActiveSourceLocation(dte, out var filePath, out var lineNumber, out var columnNumber))
            {
                data["currentStackFrame"]!["file"] = filePath;
                data["currentStackFrame"]!["line"] = lineNumber;
                data["currentStackFrame"]!["column"] = columnNumber;
            }
        }

        return data;
    }

    public async Task<JObject> StartAsync(DTE2 dte, bool waitForBreak, int timeoutMs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        dte.Debugger.Go(false);
        return waitForBreak
            ? await WaitForBreakOrDesignModeAsync(dte, timeoutMs, throwOnTimeout: true).ConfigureAwait(true)
            : await GetStateAsync(dte).ConfigureAwait(true);
    }

    public async Task<JObject> StopAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        dte.Debugger.Stop(false);
        return await GetStateAsync(dte).ConfigureAwait(true);
    }

    public async Task<JObject> BreakAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        dte.Debugger.Break(false);
        return await WaitForBreakOrDesignModeAsync(dte, 10000, throwOnTimeout: true).ConfigureAwait(true);
    }

    public async Task<JObject> ContinueAsync(DTE2 dte, bool waitForBreak, int timeoutMs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        dte.Debugger.Go(false);
        return waitForBreak
            ? await WaitForBreakOrDesignModeAsync(dte, timeoutMs, throwOnTimeout: true).ConfigureAwait(true)
            : await GetStateAsync(dte).ConfigureAwait(true);
    }

    public async Task<JObject> StepOverAsync(DTE2 dte, int timeoutMs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        EnsureBreakMode(dte);
        dte.Debugger.StepOver(false);
        return await WaitForBreakOrDesignModeAsync(dte, timeoutMs, throwOnTimeout: true).ConfigureAwait(true);
    }

    public async Task<JObject> StepIntoAsync(DTE2 dte, int timeoutMs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        EnsureBreakMode(dte);
        dte.Debugger.StepInto(false);
        return await WaitForBreakOrDesignModeAsync(dte, timeoutMs, throwOnTimeout: true).ConfigureAwait(true);
    }

    public async Task<JObject> StepOutAsync(DTE2 dte, int timeoutMs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        EnsureBreakMode(dte);
        dte.Debugger.StepOut(false);
        return await WaitForBreakOrDesignModeAsync(dte, timeoutMs, throwOnTimeout: true).ConfigureAwait(true);
    }

    private static void EnsureBreakMode(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            throw new CommandErrorException("not_in_break_mode", "Debugger is not currently in break mode.");
        }
    }

    private static JArray GetDebuggedProcessNames(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var items = new JArray();
        foreach (Process process in debugger.DebuggedProcesses)
        {
            items.Add(process.Name ?? string.Empty);
        }

        return items;
    }

    private static JArray GetThreadSummaries(Program? program)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var items = new JArray();
        if (program is null)
        {
            return items;
        }

        foreach (Thread thread in program.Threads)
        {
            items.Add(new JObject
            {
                ["id"] = thread.ID,
                ["name"] = thread.Name ?? string.Empty,
            });
        }

        return items;
    }

    private static bool TryGetActiveSourceLocation(DTE2 dte, out string filePath, out int lineNumber, out int columnNumber)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        filePath = string.Empty;
        lineNumber = 0;
        columnNumber = 0;

        if (dte.ActiveDocument?.Object("TextDocument") is not TextDocument textDocument)
        {
            return false;
        }

        var selection = textDocument.Selection;
        filePath = dte.ActiveDocument.FullName ?? string.Empty;
        lineNumber = selection.ActivePoint.Line;
        columnNumber = selection.ActivePoint.DisplayColumn;
        return !string.IsNullOrWhiteSpace(filePath);
    }

    private async Task<JObject> WaitForBreakOrDesignModeAsync(DTE2 dte, int timeoutMs, bool throwOnTimeout)
    {
        var timeout = timeoutMs <= 0 ? 10000 : timeoutMs;
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var mode = dte.Debugger.CurrentMode;
            if (mode != dbgDebugMode.dbgRunMode)
            {
                var data = await GetStateAsync(dte).ConfigureAwait(true);
                data["timeoutMs"] = timeout;
                data["waitedForBreak"] = true;
                data["timedOut"] = false;
                return data;
            }

            if (stopwatch.ElapsedMilliseconds >= timeout)
            {
                if (throwOnTimeout)
                {
                    throw new CommandErrorException("timeout", $"Debugger did not reach break or design mode within {timeout} ms.");
                }

                var data = await GetStateAsync(dte).ConfigureAwait(true);
                data["timeoutMs"] = timeout;
                data["waitedForBreak"] = true;
                data["timedOut"] = true;
                return data;
            }

            await Task.Delay(100).ConfigureAwait(true);
        }
    }
}
