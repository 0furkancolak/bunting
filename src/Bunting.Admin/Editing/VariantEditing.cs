using System.Globalization;
using System.Text.Json;
using Bunting.Domain;

namespace Bunting.Admin.Editing;

/// <summary>One variant row of the flag form: a key and its value, edited as text appropriate for the flag type.</summary>
public sealed class VariantVm
{
    public string Key { get; set; } = string.Empty;

    public string ValueText { get; set; } = string.Empty;

    public static VariantVm From(string key, JsonElement value, FlagType type) => new()
    {
        Key = key,
        ValueText = type switch
        {
            FlagType.String => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText(),
            _ => value.GetRawText(),
        },
    };

    /// <summary>Parses the text according to the flag type. Returns an error message when it does not fit.</summary>
    public string? TryParse(FlagType type, out JsonElement value)
    {
        value = default;
        var text = ValueText.Trim();
        try
        {
            switch (type)
            {
                case FlagType.Boolean:
                    if (!bool.TryParse(text, out var b))
                    {
                        return "Use true or false.";
                    }

                    value = JsonSerializer.SerializeToElement(b);
                    return null;
                case FlagType.String:
                    value = JsonSerializer.SerializeToElement(ValueText);
                    return null;
                case FlagType.Integer:
                    if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    {
                        return "Use a whole number.";
                    }

                    value = JsonSerializer.SerializeToElement(i);
                    return null;
                case FlagType.Double:
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        return "Use a number.";
                    }

                    value = JsonSerializer.SerializeToElement(d);
                    return null;
                default:
                    using (var document = JsonDocument.Parse(text))
                    {
                        value = document.RootElement.Clone();
                    }

                    return null;
            }
        }
        catch (JsonException)
        {
            return "Enter valid JSON.";
        }
    }
}

public static class VariantForm
{
    /// <summary>Builds the variants dictionary, or reports an error per row (key: row index).</summary>
    public static bool TryBuild(IReadOnlyList<VariantVm> rows, FlagType type, out Dictionary<string, JsonElement> variants, out Dictionary<int, string> errors)
    {
        variants = [];
        errors = [];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (string.IsNullOrWhiteSpace(row.Key))
            {
                errors[i] = "A variant needs a key.";
                continue;
            }

            if (variants.ContainsKey(row.Key.Trim()))
            {
                errors[i] = "Variant keys must be unique.";
                continue;
            }

            if (row.TryParse(type, out var value) is { } error)
            {
                errors[i] = error;
                continue;
            }

            variants[row.Key.Trim()] = value;
        }

        return errors.Count == 0 && variants.Count > 0;
    }

    public static List<VariantVm> Defaults(FlagType type) => type switch
    {
        FlagType.Boolean => [new VariantVm { Key = "on", ValueText = "true" }, new VariantVm { Key = "off", ValueText = "false" }],
        FlagType.String => [new VariantVm { Key = "a", ValueText = "A" }, new VariantVm { Key = "b", ValueText = "B" }],
        FlagType.Integer => [new VariantVm { Key = "low", ValueText = "1" }, new VariantVm { Key = "high", ValueText = "10" }],
        FlagType.Double => [new VariantVm { Key = "low", ValueText = "0.1" }, new VariantVm { Key = "high", ValueText = "1.0" }],
        _ => [new VariantVm { Key = "default", ValueText = "{}" }, new VariantVm { Key = "alt", ValueText = "{}" }],
    };
}
