using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VsIdeBridge.Infrastructure;

internal sealed class CommandArguments(Dictionary<string, List<string>> values)
{
    private const string InvalidArgumentsCode = "invalid_arguments";
    private readonly Dictionary<string, List<string>> _values = values;

    public string? GetString(string name, string? defaultValue = null)
    {
        return _values.TryGetValue(name, out List<string>? values) && values.Count > 0
            ? values[values.Count - 1]
            : defaultValue;
    }

    public bool Has(string name)
    {
        return _values.ContainsKey(name);
    }

    public string GetRequiredString(string name)
    {
        string? value = GetString(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandErrorException(InvalidArgumentsCode, $"Missing required argument --{name}.");
        }

        return value!;
    }

    public int GetInt32(string name, int defaultValue)
    {
        string? raw = GetString(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new CommandErrorException(InvalidArgumentsCode, $"Argument --{name} must be an integer.");
        }

        return value;
    }

    public int? GetNullableInt32(string name)
    {
        string? raw = GetString(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new CommandErrorException(InvalidArgumentsCode, $"Argument --{name} must be an integer.");
        }

        return value;
    }

    public bool GetBoolean(string name, bool defaultValue)
    {
        string? raw = GetString(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (bool.TryParse(raw, out bool value))
        {
            return value;
        }

        throw new CommandErrorException(InvalidArgumentsCode, $"Argument --{name} must be true or false.");
    }

    public string GetEnum(string name, string defaultValue, params string[] allowedValues)
    {
        string value = GetString(name, defaultValue) ?? defaultValue;
        if (!allowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            throw new CommandErrorException(InvalidArgumentsCode, $"Argument --{name} must be one of: {string.Join(", ", allowedValues)}.");
        }

        return value.ToLowerInvariant();
    }

    public IReadOnlyDictionary<string, List<string>> RawValues => _values;
}
