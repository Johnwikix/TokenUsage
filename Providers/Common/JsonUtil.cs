using System.Globalization;
using System.Text.Json;

namespace TokenUsage.Providers.Common;

internal static class JsonUtil
{
    public static bool TryProp(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            return obj.TryGetProperty(name, out value);
        }
        value = default;
        return false;
    }

    public static JsonElement? Prop(JsonElement obj, string name) =>
        TryProp(obj, name, out var value) ? value : null;

    public static JsonElement? FirstProp(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryProp(obj, name, out var value)) return value;
        }
        return null;
    }

    public static string? Str(JsonElement obj, string name) =>
        Prop(obj, name) is { } value ? ToStr(value) : null;

    public static string? FirstStr(JsonElement obj, params string[] names) =>
        FirstProp(obj, names) is { } value ? ToStr(value) : null;

    public static double? Num(JsonElement obj, string name) =>
        Prop(obj, name) is { } value ? ToNum(value) : null;

    public static double? FirstNum(JsonElement obj, params string[] names) =>
        FirstProp(obj, names) is { } value ? ToNum(value) : null;

    public static long? Long(JsonElement obj, string name) =>
        Prop(obj, name) is { } value ? ToLong(value) : null;

    public static long? FirstLong(JsonElement obj, params string[] names) =>
        FirstProp(obj, names) is { } value ? ToLong(value) : null;

    public static bool? Bool(JsonElement obj, string name) =>
        Prop(obj, name) is { } value ? ToBool(value) : null;

    public static string? ToStr(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };

    public static double? ToNum(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
            case JsonValueKind.String:
                var text = value.GetString();
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    && double.IsFinite(parsed)
                    ? parsed
                    : null;
            default:
                return null;
        }
    }

    public static long? ToLong(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var integer)) return integer;
                if (value.TryGetDouble(out var number) && double.IsFinite(number)) return (long)Math.Round(number);
                return null;
            case JsonValueKind.String:
                var text = value.GetString();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue)
                    && double.IsFinite(decimalValue))
                {
                    return (long)Math.Round(decimalValue);
                }
                return null;
            default:
                return null;
        }
    }

    public static bool? ToBool(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetDouble(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : null,
            _ => null,
        };

    public static List<JsonElement> Array(JsonElement obj, string name)
    {
        var result = new List<JsonElement>();
        if (Prop(obj, name) is { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var item in array.EnumerateArray()) result.Add(item);
        }
        return result;
    }
}
