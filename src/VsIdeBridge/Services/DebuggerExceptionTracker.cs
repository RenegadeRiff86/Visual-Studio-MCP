using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;

namespace VsIdeBridge.Services;

internal sealed class DebuggerExceptionTracker
{
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

        _debuggerEvents = dte.Events.DebuggerEvents;
        _debuggerEvents.OnExceptionThrown += OnExceptionThrown;
        _debuggerEvents.OnExceptionNotHandled += OnExceptionNotHandled;
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

    private void OnExceptionThrown(string exceptionType, string name, int code, string description, ref dbgExceptionAction exceptionAction)
    {
        CaptureException("thrown", exceptionType, name, code, description, exceptionAction);
    }

    private void OnExceptionNotHandled(string exceptionType, string name, int code, string description, ref dbgExceptionAction exceptionAction)
    {
        CaptureException("notHandled", exceptionType, name, code, description, exceptionAction);
    }

    private void OnEnterRunMode(dbgEventReason reason)
    {
        ClearLastException();
    }

    private void CaptureException(string eventKind, string exceptionType, string name, int code, string description, dbgExceptionAction exceptionAction)
    {
        JObject exception = new()
        {
            ["event"] = eventKind,
            ["exceptionType"] = exceptionType,
            ["name"] = name,
            ["code"] = code,
            ["description"] = description,
            ["exceptionAction"] = exceptionAction.ToString(),
            ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        lock (_exceptionLock)
        {
            _lastException = exception;
        }
    }

    private void ClearLastException()
    {
        lock (_exceptionLock)
        {
            _lastException = null;
        }
    }
}
