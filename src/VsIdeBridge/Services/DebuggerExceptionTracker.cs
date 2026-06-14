using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Runtime.InteropServices;

namespace VsIdeBridge.Services;

/// <summary>
/// Surfaces the exception the debugger is currently stopped on as 'lastException' on debugger
/// responses. Instead of relying on the legacy DTE OnExceptionThrown/OnExceptionNotHandled event
/// sinks -- which only fire for the exception categories the user configured to break on and carry
/// only a thin type/code/description -- this actively evaluates the $exception pseudovariable while
/// in break mode. $exception is the same object the Visual Studio Exception Helper shows, so we get
/// the real runtime type, message, stack trace, HResult, and inner exception. All evaluation runs
/// on the Visual Studio UI thread and never throws.
/// </summary>
internal sealed class DebuggerExceptionTracker
{
    private const int EvaluateTimeoutMilliseconds = 2000;

    private readonly object _exceptionLock = new();
    private DebuggerEvents? _debuggerEvents;
    private JObject? _lastException;

    public void PrepareForRun(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        EnsureSubscribed(dte);
        ClearLastException();
    }

    public void EnsureSubscribed(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_debuggerEvents is not null)
        {
            return;
        }

        // Keep the events object alive on the field; DTE event sinks stop firing when only held in
        // a local. OnEnterRunMode is the reliable signal that execution resumed, so a stale
        // exception from the previous break is never reported as the current one.
        _debuggerEvents = dte.Events.DebuggerEvents;
        _debuggerEvents.OnEnterRunMode += OnEnterRunMode;
    }

    public void AddLastException(JObject target)
    {
        JObject? exception;
        lock (_exceptionLock)
        {
            exception = _lastException is null ? null : (JObject)_lastException.DeepClone();
        }

        if (exception is not null)
        {
            target["lastException"] = exception;
        }
    }

    /// <summary>
    /// Reads the in-flight exception from the live debugger by evaluating $exception. Safe to call
    /// whenever debugger state is queried: it no-ops unless the debugger is in break mode with an
    /// exception object present, and swallows evaluation failures.
    /// </summary>
    public void CaptureFromDebugger(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            // Run/design-mode clearing is handled by OnEnterRunMode; nothing to capture here.
            return;
        }

        dbgEventReason reason = SafeLastBreakReason(debugger);
        bool brokeOnException = reason is dbgEventReason.dbgEventReasonExceptionThrown
            or dbgEventReason.dbgEventReasonExceptionNotHandled;

        Expression? root = TryEvaluate(debugger, "$exception");
        if (root is null || !root.IsValidValue)
        {
            // No exception object in flight (a plain breakpoint, a step, or a native break with no
            // $exception). Leave anything already captured for this same break untouched.
            return;
        }

        string eventKind = brokeOnException
            ? (reason == dbgEventReason.dbgEventReasonExceptionNotHandled ? "notHandled" : "thrown")
            : "inFlight";

        JObject exception = new()
        {
            ["event"] = eventKind,
            ["type"] = string.IsNullOrEmpty(root.Type) ? "(unknown)" : root.Type,
            ["message"] = ReadValue(debugger, "$exception.Message") ?? Unquote(root.Value),
            ["breakReason"] = reason.ToString(),
            ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        string? stackTrace = ReadValue(debugger, "$exception.StackTrace");
        if (!string.IsNullOrWhiteSpace(stackTrace))
        {
            exception["stackTrace"] = stackTrace;
        }

        string? hResult = ReadValue(debugger, "$exception.HResult");
        if (!string.IsNullOrWhiteSpace(hResult))
        {
            exception["hResult"] = hResult;
        }

        Expression? inner = TryEvaluate(debugger, "$exception.InnerException");
        if (inner is not null && inner.IsValidValue && !IsNullValue(inner.Value))
        {
            exception["innerException"] = new JObject
            {
                ["type"] = string.IsNullOrEmpty(inner.Type) ? "(unknown)" : inner.Type,
                ["message"] = ReadValue(debugger, "$exception.InnerException.Message"),
            };
        }

        lock (_exceptionLock)
        {
            _lastException = exception;
        }
    }

    private void OnEnterRunMode(dbgEventReason reason) => ClearLastException();

    private static dbgEventReason SafeLastBreakReason(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            return debugger.LastBreakReason;
        }
        catch (COMException)
        {
            return dbgEventReason.dbgEventReasonNone;
        }
    }

    private static Expression? TryEvaluate(Debugger debugger, string expression)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            return debugger.GetExpression(expression, false, EvaluateTimeoutMilliseconds);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string? ReadValue(Debugger debugger, string expression)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        Expression? result = TryEvaluate(debugger, expression);
        if (result is null || !result.IsValidValue)
        {
            return null;
        }

        return Unquote(result.Value);
    }

    private static string Unquote(string? value)
    {
        value ??= string.Empty;

        // The expression evaluator renders string values wrapped in quotes; strip a single
        // enclosing pair so the message/stack trace read cleanly.
        if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
        {
            value = value.Substring(1, value.Length - 2);
        }

        return value;
    }

    private static bool IsNullValue(string? value) =>
        string.IsNullOrEmpty(value) || string.Equals(value!.Trim(), "null", StringComparison.OrdinalIgnoreCase);

    private void ClearLastException()
    {
        lock (_exceptionLock)
        {
            _lastException = null;
        }
    }
}
