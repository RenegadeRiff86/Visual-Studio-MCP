using Microsoft.VisualStudio.Shell;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VsIdeBridge.Commands;

internal static partial class SolutionProjectCommands
{
    private static object? TryGetAutomationProperty(object? target, string propertyName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (target is null)
        {
            return null;
        }

        try
        {
            return target.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, binder: null, target, args: null);
        }
        catch (MissingMethodException ex)
        {
            Debug.WriteLine($"Automation property '{propertyName}' is unavailable: {ex.Message}");
            return null;
        }
        catch (ArgumentException ex)
        {
            Debug.WriteLine($"Automation property '{propertyName}' is invalid: {ex.Message}");
            return null;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Automation property '{propertyName}' could not be read: {ex.Message}");
            return null;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException or ArgumentException or MissingMethodException)
        {
            Debug.WriteLine($"Automation property '{propertyName}' threw: {ex.InnerException?.Message ?? ex.Message}");
            return null;
        }
    }

    private static string? TryGetAutomationString(object? target, string propertyName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return TryGetAutomationProperty(target, propertyName)?.ToString();
    }

    private static bool? TryGetAutomationBoolean(object? target, string propertyName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        object? value = TryGetAutomationProperty(target, propertyName);
        return value switch
        {
            bool boolean => boolean,
            _ when bool.TryParse(value?.ToString(), out bool parsed) => parsed,
            _ => null,
        };
    }

    private static int? TryGetAutomationInt32(object? target, string propertyName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        object? value = TryGetAutomationProperty(target, propertyName);
        return value switch
        {
            byte byteValue => byteValue,
            short shortValue => shortValue,
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            _ when int.TryParse(value?.ToString(), out int parsed) => parsed,
            _ => null,
        };
    }

    private static IEnumerable<object> EnumerateAutomationObjects(object? collection)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (collection is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (object automationObject in enumerable)
        {
            if (automationObject is not null)
            {
                yield return automationObject;
            }
        }
    }
}
