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
    public async Task<JObject> SetBreakpointAsync(
        DTE2 dte,
        string filePath,
        int line,
        int column,
        string? condition,
        string conditionType,
        int hitCount,
        string hitType,
        string? traceMessage,
        bool continueExecution)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        string normalizedPath = PathNormalization.NormalizeFilePath(filePath);
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

        string normalizedPath = PathNormalization.NormalizeFilePath(filePath);
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

        string normalizedPath = PathNormalization.NormalizeFilePath(filePath);
        Breakpoint bp = FindBreakpoint(dte, normalizedPath, line) ?? throw new CommandErrorException("not_found", $"No breakpoint found at {normalizedPath}:{line}");
        bp.Enabled = true;
        return SerializeBreakpoint(bp, normalizedPath, line);
    }

    public async Task<JObject> DisableBreakpointAsync(DTE2 dte, string filePath, int line)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        string normalizedPath = PathNormalization.NormalizeFilePath(filePath);
        Breakpoint bp = FindBreakpoint(dte, normalizedPath, line) ?? throw new CommandErrorException("not_found", $"No breakpoint found at {normalizedPath}:{line}");
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
