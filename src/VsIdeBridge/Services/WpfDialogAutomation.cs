using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

/// <summary>
/// Reads and dismisses a modal WPF Visual Studio dialog (window class "HwndWrapper...") via UI
/// Automation. WPF controls are not child HWNDs, so Win32 EnumChildWindows cannot read them. UI
/// Automation client calls against a window owned by the same thread can deadlock, so the
/// read/dismiss runs on a short-lived background thread while the Visual Studio UI thread pumps
/// the dialog's modal loop. Only dialogs whose text mentions a breakpoint are acted on; anything
/// else is read once and left for a human. This is the WPF counterpart to the classic #32770
/// handling in <see cref="BreakpointDialogTracker"/>.
/// </summary>
internal static class WpfDialogAutomation
{
    private const int TimeoutMs = 5000;
    private const int PollMs = 100;

    // Windows currently being handled by a background worker, so repeated HCBT_ACTIVATE
    // notifications for the same dialog do not spawn duplicate workers.
    private static readonly object InflightLock = new();
    private static readonly HashSet<IntPtr> Inflight = [];

    /// <summary>
    /// Schedules a background UI Automation read/dismiss of the dialog identified by
    /// <paramref name="hwnd"/>. Repeated calls for the same window while a worker is already
    /// running are ignored. <paramref name="onCapture"/> receives the dialog text when a
    /// breakpoint dialog is found.
    /// </summary>
    public static void ScheduleDismiss(IntPtr hwnd, Action<string> onCapture)
    {
        lock (InflightLock)
        {
            if (!Inflight.Add(hwnd))
            {
                return;
            }
        }

        System.Threading.Thread worker = new(() => Run(hwnd, onCapture))
        {
            IsBackground = true,
            Name = "VsIdeBridge.WpfDialogDismiss",
        };
        worker.Start();
    }

    private static void Run(IntPtr hwnd, Action<string> onCapture)
    {
        try
        {
            PollUntilHandled(hwnd, onCapture);
        }
        catch (ElementNotAvailableException)
        {
            // The dialog closed while we were reading it -- nothing left to do.
            BridgeActivityLog.LogInfo(nameof(WpfDialogAutomation), "dialog closed before it could be read.");
        }
        catch (ElementNotEnabledException ex)
        {
            BridgeActivityLog.LogInfo(nameof(WpfDialogAutomation), $"dismiss failed (element not enabled): {ex.Message}");
        }
        catch (COMException ex)
        {
            BridgeActivityLog.LogInfo(nameof(WpfDialogAutomation), $"dismiss failed (COM): {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            BridgeActivityLog.LogInfo(nameof(WpfDialogAutomation), $"dismiss failed: {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            BridgeActivityLog.LogInfo(nameof(WpfDialogAutomation), $"dismiss failed (UIA timeout): {ex.Message}");
        }
        finally
        {
            lock (InflightLock)
            {
                Inflight.Remove(hwnd);
            }
        }
    }

    private static void PollUntilHandled(IntPtr hwnd, Action<string> onCapture)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < TimeoutMs)
        {
            if (TryHandleOnce(hwnd, onCapture))
            {
                return;
            }

            // The dialog exists but its text is not populated yet; it may still be building.
            System.Threading.Thread.Sleep(PollMs);
        }

        BridgeActivityLog.LogInfo(nameof(WpfDialogAutomation), "dialog read timed out before text was available.");
    }

    /// <summary>
    /// Reads the dialog once. Returns true when handling is complete (the dialog was dismissed,
    /// ignored as unrelated, or has vanished); false when the dialog text is not yet available
    /// and the caller should retry.
    /// </summary>
    private static bool TryHandleOnce(IntPtr hwnd, Action<string> onCapture)
    {
        AutomationElement? root = TryFromHandle(hwnd);
        if (root is null)
        {
            return true; // window gone -- stop
        }

        string text = ReadText(root);
        bool isBreakpoint = text.IndexOf("breakpoint", StringComparison.OrdinalIgnoreCase) >= 0;
        if (isBreakpoint)
        {
            onCapture(text.Trim());
            bool dismissed = TryInvokeDismissButton(root, out string buttonName);
            BridgeActivityLog.LogInfo(nameof(WpfDialogAutomation),
                $"dialog dismissed={dismissed} button='{buttonName}' text='{Snippet(text)}'");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            // A different "Microsoft Visual Studio" dialog -- leave it for a human.
            BridgeActivityLog.LogInfo(nameof(WpfDialogAutomation),
                $"dialog ignored (not a breakpoint dialog) text='{Snippet(text)}'");
            return true;
        }

        return false; // text not populated yet -- retry
    }

    private static AutomationElement? TryFromHandle(IntPtr hwnd)
    {
        try
        {
            return AutomationElement.FromHandle(hwnd);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null; // handle no longer valid
        }
    }

    private static string ReadText(AutomationElement root)
    {
        StringBuilder all = new();
        AutomationElementCollection texts = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        foreach (AutomationElement element in texts)
        {
            string name = SafeName(element);
            if (!string.IsNullOrWhiteSpace(name))
            {
                all.Append(name).Append('\n');
            }
        }

        return all.ToString();
    }

    private static bool TryInvokeDismissButton(AutomationElement root, out string buttonName)
    {
        buttonName = string.Empty;
        AutomationElement? chosen = ChooseDismissButton(root);
        if (chosen is null)
        {
            return false;
        }

        buttonName = SafeName(chosen);
        if (chosen.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
        {
            ((InvokePattern)pattern).Invoke();
            return true;
        }

        return false;
    }

    private static AutomationElement? ChooseDismissButton(AutomationElement root)
    {
        AutomationElementCollection buttons = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        foreach (AutomationElement button in buttons)
        {
            string name = Normalize(SafeName(button));
            if (name == "ok" || name == "yes")
            {
                return button;
            }
        }

        // A single-button dialog's only button is its dismiss button.
        return buttons.Count == 1 ? buttons[0] : null;
    }

    private static string SafeName(AutomationElement element)
    {
        try
        {
            return element.Current.Name ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    private static string Normalize(string name) =>
        name.Replace("&", string.Empty).Trim().ToLowerInvariant();

    private static string Snippet(string text)
    {
        string oneLine = text.Replace("\r", " ").Replace("\n", " ");
        return oneLine.Length > 200 ? oneLine.Substring(0, 200) : oneLine;
    }
}
