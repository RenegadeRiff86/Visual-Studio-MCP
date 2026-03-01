using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class WindowService
{
    public async Task<JObject> ListWindowsAsync(DTE2 dte, string? query)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var windows = dte.Windows
            .Cast<Window>()
            .Select(CreateWindowInfo)
            .Where(window => MatchesWindow(window, query))
            .ToArray();

        return new JObject
        {
            ["query"] = query ?? string.Empty,
            ["count"] = windows.Length,
            ["items"] = new JArray(windows),
        };
    }

    public async Task<JObject> ActivateWindowAsync(DTE2 dte, string windowName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var window = ResolveWindow(dte, windowName, allowContains: true);
        window.Activate();
        return CreateWindowInfo(window);
    }

    public async Task<JObject?> WaitForWindowAsync(DTE2 dte, string query, bool activate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
        do
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var window = TryResolveWindow(dte, query, allowContains: true);
            if (window is not null)
            {
                if (activate)
                {
                    window.Activate();
                }

                return CreateWindowInfo(window);
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(200).ConfigureAwait(true);
        }
        while (true);

        return null;
    }

    private static bool MatchesWindow(JObject window, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var text = query.Trim();
        return Contains(window["caption"], text) ||
               Contains(window["kind"], text) ||
               Contains(window["objectKind"], text) ||
               Contains(window["documentPath"], text);
    }

    private static JObject CreateWindowInfo(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return new JObject
        {
            ["caption"] = window.Caption ?? string.Empty,
            ["kind"] = window.Kind ?? string.Empty,
            ["objectKind"] = window.ObjectKind ?? string.Empty,
            ["type"] = window.Type.ToString(),
            ["visible"] = window.Visible,
            ["documentPath"] = string.IsNullOrWhiteSpace(window.Document?.FullName)
                ? string.Empty
                : PathNormalization.NormalizeFilePath(window.Document.FullName),
        };
    }

    private static Window ResolveWindow(DTE2 dte, string query, bool allowContains)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var window = TryResolveWindow(dte, query, allowContains);
        if (window is not null)
        {
            return window;
        }

        throw new CommandErrorException("window_not_found", $"Window not found: {query}");
    }

    private static Window? TryResolveWindow(DTE2 dte, string query, bool allowContains)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var trimmed = query.Trim();
        var windows = dte.Windows.Cast<Window>().ToArray();

        foreach (var window in windows)
        {
            if (EqualsWindow(window, trimmed))
            {
                return window;
            }
        }

        if (!allowContains)
        {
            return null;
        }

        var partial = windows.Where(window => Contains(window.Caption, trimmed) ||
                                              Contains(window.Kind, trimmed) ||
                                              Contains(window.ObjectKind, trimmed) ||
                                              Contains(window.Document?.FullName, trimmed))
            .ToArray();

        if (partial.Length == 1)
        {
            return partial[0];
        }

        if (partial.Length > 1)
        {
            throw new CommandErrorException(
                "invalid_arguments",
                $"Window query '{query}' matched multiple windows.",
                new
                {
                    query,
                    matches = partial.Select(window => window.Caption).ToArray(),
                });
        }

        return null;
    }

    private static bool EqualsWindow(Window window, string query)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return string.Equals(window.Caption, query, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(window.ObjectKind, query, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(window.Kind, query, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(window.Document?.FullName, query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string? value, string query)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool Contains(JToken? token, string query)
    {
        return token is not null && Contains(token.ToString(), query);
    }
}
