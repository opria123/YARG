using System;
using System.Text.Json;

namespace YARG.Net.Serialization;

/// <summary>
/// `INetSerializer` backed by <see cref="JsonSerializer"/>.
/// </summary>
public sealed class JsonNetSerializer : INetSerializer
{
    private readonly JsonSerializerOptions _options;

    public JsonNetSerializer(JsonSerializerOptions? options = null)
    {
        _options = options ?? NetSerializerOptions.CreateDefault();
    }

    public ReadOnlyMemory<byte> Serialize<T>(T payload)
    {
        return JsonSerializer.SerializeToUtf8Bytes(payload, _options);
    }

    public T Deserialize<T>(ReadOnlySpan<byte> payload)
    {
        var result = JsonSerializer.Deserialize<T>(payload, _options);
        if (result is null && typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null)
        {
            throw new JsonException($"Could not deserialize payload into {typeof(T)}.");
        }

        return result!;
    }
}
