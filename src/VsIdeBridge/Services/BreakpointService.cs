using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class BreakpointService
{
    public HandleService? HandleService { get; set; }

    public async Task<JObject> SetBreakpointAsync(
        DTE2 dte,
        string? filePath,
        int line,
        int column,
        string? condition,
        string conditionType,
        int hitCount,
        string hitType,
        string? traceMessage,
        bool continueExecution,
        string? function = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // Function (symbol) breakpoint: bind by function name instead of file:line. These
        // survive source edits/line shifts, which file:line breakpoints do not.
        if (!string.IsNullOrWhiteSpace(function))
        {
            return SetFunctionBreakpointOnUiThread(
                dte, function!, condition, conditionType, hitCount, hitType, traceMessage, continueExecution);
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new CommandErrorException(
                "invalid_request",
                "set_breakpoint requires either 'file' (with 'line') or 'function'.");
        }

        string normalizedPath = ResolveBreakpointPath(filePath!);
        Breakpoint? existing = FindBreakpoint(dte, normalizedPath, line);
        existing?.Delete();

        dte.Debugger.Breakpoints.Add(
            File: normalizedPath,
            Line: line,
            Column: column,
            Condition: condition ?? string.Empty,
            ConditionType: MapConditionType(conditionType),
            HitCount: hitCount,
            HitCountType: MapHitCountType(hitType));

        existing = FindBreakpoint(dte, normalizedPath, line)
            ?? FindNearestBreakpoint(dte, normalizedPath, line);

        if (existing is null)
        {
            return CreatePendingBreakpointResult(
                normalizedPath,
                line,
                column,
                condition,
                conditionType,
                hitCount,
                hitType,
                traceMessage,
                continueExecution);
        }

        existing.Enabled = true;
        if (existing is Breakpoint2 advancedBreakpoint)
        {
            advancedBreakpoint.Message = traceMessage ?? string.Empty;
            advancedBreakpoint.BreakWhenHit = !continueExecution;
        }

        return SerializeBreakpoint(existing, normalizedPath, line);
    }

    public async Task<JObject> ListBreakpointsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        JArray items = [..dte.Debugger.Breakpoints.Cast<Breakpoint>().Select(breakpoint => SerializeBreakpoint(breakpoint))];
        return new()
        {
            ["count"] = items.Count,
            ["items"] = items,
        };
    }

    public async Task<JObject> RemoveBreakpointAsync(DTE2 dte, string filePath, int line)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        string normalizedPath = ResolveBreakpointPath(filePath);
        Breakpoint[] matches = [..dte.Debugger.Breakpoints
            .Cast<Breakpoint>()
            .Where(breakpoint => MatchesBreakpointLocation(breakpoint, normalizedPath, line))];

        foreach (var breakpoint in matches)
        {
            breakpoint.Delete();
        }

        return new JObject
        {
            ["removedCount"] = matches.Length,
            ["remainingCount"] = dte.Debugger.Breakpoints.Count,
            ["file"] = normalizedPath,
            ["line"] = line,
        };
    }

    public async Task<JObject> ClearAllBreakpointsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        int count = dte.Debugger.Breakpoints.Count;
        foreach (Breakpoint breakpoint in dte.Debugger.Breakpoints.Cast<Breakpoint>().ToList())
        {
            breakpoint.Delete();
        }

        return new JObject
        {
            ["removedCount"] = count,
            ["remainingCount"] = dte.Debugger.Breakpoints.Count,
        };
    }

    public async Task<JObject> EnableBreakpointAsync(DTE2 dte, string filePath, int line)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        string normalizedPath = ResolveBreakpointPath(filePath);
        Breakpoint bp = FindBreakpoint(dte, normalizedPath, line)
            ?? throw CreateBreakpointNotFound(normalizedPath, line);
        bp.Enabled = true;
        return SerializeBreakpoint(bp, normalizedPath, line);
    }

    public async Task<JObject> DisableBreakpointAsync(DTE2 dte, string filePath, int line)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        string normalizedPath = ResolveBreakpointPath(filePath);
        Breakpoint bp = FindBreakpoint(dte, normalizedPath, line)
            ?? throw CreateBreakpointNotFound(normalizedPath, line);
        bp.Enabled = false;
        return SerializeBreakpoint(bp, normalizedPath, line);
    }

    public async Task<JObject> EnableAllBreakpointsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        int count = 0;
        foreach (Breakpoint bp in dte.Debugger.Breakpoints)
        {
            bp.Enabled = true;
            count++;
        }

        return new JObject
        {
            ["enabledCount"] = count,
            ["totalCount"] = dte.Debugger.Breakpoints.Count,
        };
    }

    public async Task<JObject> DisableAllBreakpointsAsync(DTE2 dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        int count = 0;
        foreach (Breakpoint bp in dte.Debugger.Breakpoints)
        {
            bp.Enabled = false;
            count++;
        }

        return new JObject
        {
            ["disabledCount"] = count,
            ["totalCount"] = dte.Debugger.Breakpoints.Count,
        };
    }

    private string ResolveBreakpointPath(string filePath)
    {
        if (HandleService is { } hs)
        {
            filePath = hs.ResolveFilePath(filePath);
        }

        return PathNormalization.NormalizeFilePath(filePath);
    }

    private static Breakpoint? FindBreakpoint(DTE2 dte, string normalizedPath, int line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return dte.Debugger.Breakpoints
            .Cast<Breakpoint>()
            .FirstOrDefault(breakpoint => MatchesBreakpointLocation(breakpoint, normalizedPath, line));
    }

    private static Breakpoint? FindNearestBreakpoint(DTE2 dte, string normalizedPath, int requestedLine)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        Breakpoint? nearest = null;
        int nearestDistance = int.MaxValue;

        foreach (Breakpoint breakpoint in dte.Debugger.Breakpoints)
        {
            if (!PathNormalization.AreEquivalent(breakpoint.File, normalizedPath))
            {
                continue;
            }

            int distance = Math.Abs(breakpoint.FileLine - requestedLine);
            if (distance >= nearestDistance)
            {
                continue;
            }

            nearest = breakpoint;
            nearestDistance = distance;
        }

        return nearest;
    }

    private static bool MatchesBreakpointLocation(Breakpoint breakpoint, string normalizedPath, int line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string file = breakpoint.File;
        int fileLine = breakpoint.FileLine;
        return PathNormalization.AreEquivalent(file, normalizedPath) && fileLine == line;
    }

    private static JObject SerializeBreakpoint(Breakpoint breakpoint, string? requestedPath = null, int? requestedLine = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string file = breakpoint.File ?? string.Empty;
        int line = breakpoint.FileLine;
        int column = breakpoint.FileColumn;
        string function = breakpoint.FunctionName ?? string.Empty;
        bool enabled = breakpoint.Enabled;
        string condition = breakpoint.Condition ?? string.Empty;
        string conditionType = breakpoint.ConditionType.ToString();
        int hitCountTarget = breakpoint.HitCountTarget;
        string hitCountType = breakpoint.HitCountType.ToString();
        string name = breakpoint.Name ?? string.Empty;
        Breakpoint2? advancedBreakpoint = breakpoint as Breakpoint2;
        string? traceMessage = advancedBreakpoint?.Message;
        bool breakWhenHit = advancedBreakpoint?.BreakWhenHit ?? true;
        return new JObject
        {
            ["file"] = file,
            ["line"] = line,
            ["column"] = column,
            ["status"] = "bound",
            ["resolved"] = requestedPath is null || requestedLine is null
                || MatchesBreakpointLocation(breakpoint, requestedPath, requestedLine.Value),
            ["function"] = function,
            ["enabled"] = enabled,
            ["condition"] = condition,
            ["conditionType"] = conditionType,
            ["hitCountTarget"] = hitCountTarget,
            ["hitCountType"] = hitCountType,
            ["name"] = name,
            ["traceMessage"] = string.IsNullOrWhiteSpace(traceMessage) ? JValue.CreateNull() : traceMessage,
            ["breakWhenHit"] = breakWhenHit,
        };
    }

    private static JObject CreatePendingBreakpointResult(
        string normalizedPath,
        int line,
        int column,
        string? condition,
        string conditionType,
        int hitCount,
        string hitType,
        string? traceMessage,
        bool continueExecution)
    {
        return new JObject
        {
            ["file"] = normalizedPath,
            ["line"] = line,
            ["column"] = column,
            ["status"] = "pending",
            ["resolved"] = false,
            ["function"] = string.Empty,
            ["enabled"] = true,
            ["condition"] = condition ?? string.Empty,
            ["conditionType"] = conditionType,
            ["hitCountTarget"] = hitCount,
            ["hitCountType"] = hitType,
            ["name"] = string.Empty,
            ["traceMessage"] = string.IsNullOrWhiteSpace(traceMessage) ? JValue.CreateNull() : traceMessage,
            ["breakWhenHit"] = !continueExecution,
        };
    }

    private static CommandErrorException CreateBreakpointNotFound(string normalizedPath, int line)
    {
        return new(
            "not_found",
            $"No breakpoint found at {normalizedPath}:{line}. " +
            "Call list_breakpoints to see all active breakpoints, then retry with a valid location.");
    }

    private static JObject SetFunctionBreakpointOnUiThread(
        DTE2 dte,
        string function,
        string? condition,
        string conditionType,
        int hitCount,
        string hitType,
        string? traceMessage,
        bool continueExecution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // Drop any existing breakpoint already bound to this function so repeated calls
        // update in place instead of stacking duplicates.
        foreach (Breakpoint duplicate in dte.Debugger.Breakpoints.Cast<Breakpoint>()
                     .Where(bp => FunctionMatches(bp, function)).ToList())
        {
            duplicate.Delete();
        }

        dte.Debugger.Breakpoints.Add(
            Function: function,
            Condition: condition ?? string.Empty,
            ConditionType: MapConditionType(conditionType),
            HitCount: hitCount,
            HitCountType: MapHitCountType(hitType));

        Breakpoint? created = dte.Debugger.Breakpoints.Cast<Breakpoint>()
            .FirstOrDefault(bp => FunctionMatches(bp, function));

        if (created is null)
        {
            // Created but not yet bound (e.g. module not loaded). Report pending rather than failing.
            return new JObject
            {
                ["file"] = string.Empty,
                ["line"] = 0,
                ["column"] = 0,
                ["status"] = "pending",
                ["resolved"] = false,
                ["function"] = function,
                ["enabled"] = true,
                ["condition"] = condition ?? string.Empty,
                ["conditionType"] = conditionType,
                ["hitCountTarget"] = hitCount,
                ["hitCountType"] = hitType,
                ["name"] = string.Empty,
                ["traceMessage"] = string.IsNullOrWhiteSpace(traceMessage) ? JValue.CreateNull() : traceMessage,
                ["breakWhenHit"] = !continueExecution,
            };
        }

        created.Enabled = true;
        if (created is Breakpoint2 advanced)
        {
            advanced.Message = traceMessage ?? string.Empty;
            advanced.BreakWhenHit = !continueExecution;
        }

        return SerializeBreakpoint(created);
    }

    private static bool FunctionMatches(Breakpoint breakpoint, string function)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            string name = breakpoint.FunctionName ?? string.Empty;
            return name.Length > 0
                && (string.Equals(name, function, StringComparison.Ordinal)
                    || name.EndsWith(function, StringComparison.Ordinal));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Some breakpoint kinds (data, address) throw when FunctionName is read.
            return false;
        }
    }

    private static dbgBreakpointConditionType MapConditionType(string value)
    {
        return value switch
        {
            "changed" => dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenChanged,
            _ => dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue,
        };
    }

    private static dbgHitCountType MapHitCountType(string value)
    {
        return value switch
        {
            "equal" => dbgHitCountType.dbgHitCountTypeEqual,
            "multiple" => dbgHitCountType.dbgHitCountTypeMultiple,
            "greater-or-equal" => dbgHitCountType.dbgHitCountTypeGreaterOrEqual,
            _ => dbgHitCountType.dbgHitCountTypeNone,
        };
    }
}
