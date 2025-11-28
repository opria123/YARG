using System.Text.Json;
using System.Text.Json.Serialization;

namespace YARG.Net.Serialization;

/// <summary>
/// Factory helpers for serializer option instances shared across transports.
/// </summary>
public static class NetSerializerOptions
{
    /// <summary>
    /// Creates the default <see cref="JsonSerializerOptions"/> used by YARG networking payloads.
    /// </summary>
    public static JsonSerializerOptions CreateDefault()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
