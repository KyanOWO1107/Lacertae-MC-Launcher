using System.Globalization;
using System.Text.Json;

namespace Lacertae.Infrastructure.Install.Mojang;

internal static class StrictJsonReader
{
    public static JsonElement RequiredProperty(JsonElement parent, string name, JsonValueKind kind)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != kind)
        {
            throw new InvalidDataException($"Required JSON property '{name}' is missing or has the wrong type.");
        }

        return value;
    }

    public static string RequiredString(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name, JsonValueKind.String);
        string? text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException($"Required JSON property '{name}' is blank.");
        }

        return text;
    }

    public static int RequiredInt(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name, JsonValueKind.Number);
        if (!value.TryGetInt32(out int number))
        {
            throw new InvalidDataException($"Required JSON property '{name}' is not an integer.");
        }

        return number;
    }

    public static long RequiredLong(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name, JsonValueKind.Number);
        if (!value.TryGetInt64(out long number))
        {
            throw new InvalidDataException($"Required JSON property '{name}' is not an integer.");
        }

        return number;
    }

    public static DateTimeOffset RequiredDateTimeOffset(JsonElement parent, string name)
    {
        string value = RequiredString(parent, name);
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset result))
        {
            throw new InvalidDataException($"Required JSON property '{name}' is not a timestamp.");
        }

        return result;
    }
}
