using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using System;
using System.Runtime.InteropServices;

namespace VsIdeBridge.Services;

/// <summary>
/// Surfaces the exception the debugger is currently stopped on as 'lastException' on debugger
/// responses. Managed debug engines usually expose the Visual Studio Exception Helper object through
/// the $exception pseudovariable, which gives us the runtime type, message, stack trace, HResult,
/// and inner exception. Native C++ exceptions often do not expose $exception, so this tracker also
/// subscribes to lower-level debugger events and records IDebugExceptionEvent2 details as a fallback.
/// All DTE expression evaluation runs on the Visual Studio UI thread and all callbacks swallow errors.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class DebuggerExceptionTracker : IDebugEventCallback2
{
    private const int EvaluateTimeoutMilliseconds = 2000;

    private readonly object _exceptionLock = new();
    private DebuggerEvents? _debuggerEvents;
    private IVsDebugger? _nativeDebugger;
    private bool _nativeCallbackSubscribed;
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

        if (_debuggerEvents is null)
        {
            // Keep the events object alive on the field; DTE event sinks stop firing when only held in
            // a local. OnEnterRunMode is the reliable signal that execution resumed, so a stale
            // exception from the previous break is never reported as the current one.
            _debuggerEvents = dte.Events.DebuggerEvents;
            _debuggerEvents.OnEnterRunMode += OnEnterRunMode;
        }

        EnsureNativeCallbackSubscribed();
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

    int IDebugEventCallback2.Event(
        IDebugEngine2 pEngine,
        IDebugProcess2 pProcess,
        IDebugProgram2 pProgram,
        IDebugThread2 pThread,
        IDebugEvent2 pEvent,
        ref Guid riidEvent,
        uint dwAttrib)
    {
        try
        {
            if (pEvent is IDebugExceptionEvent2 exceptionEvent)
            {
                CaptureNativeException(exceptionEvent);
            }
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Native debugger exception capture failed: {ex.Message}");
        }
        catch (InvalidCastException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Native debugger exception capture failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Native debugger exception capture failed: {ex.Message}");
        }
        finally
        {
            SafeRelease(pEvent);
            SafeRelease(pThread);
            SafeRelease(pProgram);
            SafeRelease(pProcess);
            SafeRelease(pEngine);
        }

        return VSConstants.S_OK;
    }

    private void EnsureNativeCallbackSubscribed()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_nativeCallbackSubscribed)
        {
            return;
        }

        try
        {
            if (Package.GetGlobalService(typeof(SVsShellDebugger)) is not IVsDebugger debugger)
            {
                return;
            }

            int hr = debugger.AdviseDebugEventCallback(this);
            if (ErrorHandler.Succeeded(hr))
            {
                _nativeDebugger = debugger;
                _nativeCallbackSubscribed = true;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"AdviseDebugEventCallback failed: 0x{hr:X8}");
            }
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine($"AdviseDebugEventCallback failed: {ex.Message}");
        }
    }

    private void CaptureNativeException(IDebugExceptionEvent2 exceptionEvent)
    {
        EXCEPTION_INFO[] exceptionInfo = new EXCEPTION_INFO[1];
        int hr = exceptionEvent.GetException(exceptionInfo);
        if (ErrorHandler.Failed(hr))
        {
            return;
        }

        EXCEPTION_INFO info = exceptionInfo[0];
        string? description;
        try
        {
            hr = exceptionEvent.GetExceptionDescription(out description);
        }
        catch (COMException)
        {
            description = null;
            hr = VSConstants.E_FAIL;
        }

        if (ErrorHandler.Failed(hr))
        {
            description = null;
        }

        string exceptionName = string.IsNullOrWhiteSpace(info.bstrExceptionName)
            ? "(native exception)"
            : info.bstrExceptionName;
        string message = string.IsNullOrWhiteSpace(description) ? exceptionName : description!;

        JObject exception = new()
        {
            ["event"] = NativeEventKind(info.dwState),
            ["source"] = "nativeDebugEvent",
            ["type"] = exceptionName,
            ["message"] = message,
            ["nativeCode"] = info.dwCode,
            ["nativeCodeHex"] = $"0x{info.dwCode:X8}",
            ["nativeState"] = info.dwState.ToString(),
            ["nativeStateValue"] = Convert.ToInt64(info.dwState),
            ["guidType"] = info.guidType.ToString("D"),
            ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        if (!string.IsNullOrWhiteSpace(info.bstrProgramName))
        {
            exception["program"] = info.bstrProgramName;
        }

        lock (_exceptionLock)
        {
            _lastException = exception;
        }
    }

    private static string NativeEventKind(enum_EXCEPTION_STATE state)
    {
        if (state.HasFlag(enum_EXCEPTION_STATE.EXCEPTION_STOP_SECOND_CHANCE)
            || state.HasFlag(enum_EXCEPTION_STATE.EXCEPTION_STOP_USER_UNCAUGHT))
        {
            return "notHandled";
        }

        if (state.HasFlag(enum_EXCEPTION_STATE.EXCEPTION_STOP_FIRST_CHANCE)
            || state.HasFlag(enum_EXCEPTION_STATE.EXCEPTION_STOP_USER_FIRST_CHANCE))
        {
            return "thrown";
        }

        return "native";
    }

    private static void SafeRelease(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch (ArgumentException ex)
        {
            System.Diagnostics.Debug.WriteLine($"ReleaseComObject skipped during debugger callback cleanup: {ex.Message}");
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
