using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Runtime.InteropServices;
using System.Text;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

/// <summary>
/// Captures and auto-dismisses the modal Visual Studio dialog shown during debugging when a
/// breakpoint condition or tracepoint expression cannot be evaluated -- e.g. it calls a
/// function the expression evaluator treats as having side effects ("This expression has side
/// effects and will not be evaluated"). That dialog ("The following breakpoint cannot be set...
/// The condition for a breakpoint failed to execute...") is modal and otherwise blocks the
/// bridge until a human clicks OK. We read its text, dismiss it with OK, and surface the text
/// on debugger responses as 'lastBreakpointDialog' so the caller learns the condition was
/// rejected instead of hanging on a hidden popup.
/// All install/dismiss work happens on the Visual Studio UI thread.
/// </summary>
internal sealed class BreakpointDialogTracker
{
    private readonly object _lock = new();
    private DialogSuppressor? _suppressor;
    private DebuggerEvents? _debuggerEvents;
    private JObject? _lastDialog;

    public void EnsureInstalled(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _suppressor ??= new DialogSuppressor(CaptureDialog);

        if (_debuggerEvents is null)
        {
            // Keep the events object alive on the field; DTE event sinks stop firing when only
            // held in a local. Clear the captured dialog when execution resumes so a stale
            // dialog from a previous run is never reported as the current one.
            _debuggerEvents = dte.Events.DebuggerEvents;
            _debuggerEvents.OnEnterRunMode += OnEnterRunMode;
        }
    }

    public void AddLastDialog(JObject target)
    {
        JObject? dialog;
        lock (_lock)
        {
            dialog = _lastDialog is null ? null : (JObject)_lastDialog.DeepClone();
        }

        if (dialog is not null)
        {
            target["lastBreakpointDialog"] = dialog;
        }
    }

    private void OnEnterRunMode(dbgEventReason reason) => ClearLastDialog();

    private void CaptureDialog(string message)
    {
        JObject dialog = new()
        {
            ["message"] = message,
            ["autoDismissed"] = true,
            ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        lock (_lock)
        {
            _lastDialog = dialog;
        }
    }

    private void ClearLastDialog()
    {
        lock (_lock)
        {
            _lastDialog = null;
        }
    }

    /// <summary>
    /// Thread-level WH_CBT hook (modelled on GoToDefinitionDialogSuppressor) that watches for a
    /// breakpoint condition-failure dialog as it activates, hands its text to a callback, and
    /// dismisses it with OK. Constructed on the UI thread; left installed for the lifetime of
    /// the Visual Studio session (it only acts on dialogs whose body mentions a breakpoint).
    /// </summary>
    private sealed class DialogSuppressor
    {
        private const int WH_CBT = 5;
        private const int HCBT_ACTIVATE = 5;
        private const uint WM_COMMAND = 0x0111;
        private const int IDOK = 1;
        private const string User32 = "user32.dll";

        [DllImport(User32, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, CbtProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport(User32)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport(User32, CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport(User32, CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport(User32)]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport(User32)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private delegate IntPtr CbtProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        private readonly CbtProc _proc; // Pinned delegate -- must outlive the hook
        private readonly Action<string> _onCapture;
        private readonly IntPtr _hook;

        public DialogSuppressor(Action<string> onCapture)
        {
            _onCapture = onCapture;
            _proc = HookProc;
            uint threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_CBT, _proc, IntPtr.Zero, threadId);
            BridgeActivityLog.LogInfo(nameof(BreakpointDialogTracker),
                $"WH_CBT hook installed on thread {threadId} (handle={(_hook == IntPtr.Zero ? "NULL-FAILED" : "ok")}).");
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode == HCBT_ACTIVATE)
            {
                StringBuilder classBuf = new(64);
                GetClassName(wParam, classBuf, classBuf.Capacity);
                string className = classBuf.ToString();

                StringBuilder titleBuf = new(256);
                GetWindowText(wParam, titleBuf, titleBuf.Capacity);
                string title = titleBuf.ToString();

                // Consider classic dialog boxes (#32770) and any window whose title is exactly
                // "Microsoft Visual Studio" (the breakpoint dialog's title; the main IDE window
                // is "<solution> - Microsoft Visual Studio", so exact match excludes it). Then
                // match the body text. Logged at INFO so the failure mode is visible in the log.
                bool isDialogBox = className == "#32770";
                bool isVsDialogTitle = string.Equals(title, "Microsoft Visual Studio", StringComparison.OrdinalIgnoreCase);
                if (isDialogBox || isVsDialogTitle)
                {
                    string text = ReadDialogText(wParam);
                    bool isBreakpoint = text.IndexOf("breakpoint", StringComparison.OrdinalIgnoreCase) >= 0;
                    BridgeActivityLog.LogInfo(nameof(BreakpointDialogTracker),
                        $"activate class='{className}' title='{title}' isBreakpoint={isBreakpoint} textLen={text.Length} text='{Snippet(text)}'");
                    if (isBreakpoint)
                    {
                        _onCapture(text.Trim());
                        PostMessage(wParam, WM_COMMAND, new IntPtr(IDOK), IntPtr.Zero);
                        BridgeActivityLog.LogInfo(nameof(BreakpointDialogTracker), "posted WM_COMMAND/IDOK to dismiss the breakpoint dialog.");
                    }
                }
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static string ReadDialogText(IntPtr dialog)
        {
            StringBuilder all = new();
            EnumChildWindows(dialog, (child, _) =>
            {
                StringBuilder buf = new(512);
                if (GetWindowText(child, buf, buf.Capacity) > 0)
                {
                    all.Append(buf).Append('\n');
                }
                return true;
            }, IntPtr.Zero);
            return all.ToString();
        }

        private static string Snippet(string text)
        {
            string oneLine = text.Replace("\r", " ").Replace("\n", " ");
            return oneLine.Length > 200 ? oneLine.Substring(0, 200) : oneLine;
        }
    }
}
