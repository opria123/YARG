using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace YARG.Net.Serialization;

/// <summary>
/// <see cref="INetSerializer"/> implementation backed by Newtonsoft.Json for Unity/Mono consumers.
/// </summary>
public sealed class NewtonsoftNetSerializer : INetSerializer
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly JsonSerializerSettings _settings;

    public NewtonsoftNetSerializer(JsonSerializerSettings? settings = null)
    {
        _settings = settings ?? CreateDefaultSettings();
    }

    public ReadOnlyMemory<byte> Serialize<T>(T payload)
    {
        var json = JsonConvert.SerializeObject(payload, _settings);
        return Utf8NoBom.GetBytes(json);
    }

    public T Deserialize<T>(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new JsonSerializationException("Cannot deserialize an empty payload.");
        }

        var json = Utf8NoBom.GetString(payload.ToArray());
        var result = JsonConvert.DeserializeObject<T>(json, _settings);

        if (result is null && typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null)
        {
            throw new JsonSerializationException($"Could not deserialize payload into {typeof(T)}.");
        }

        return result!;
    }

    private static JsonSerializerSettings CreateDefaultSettings()
    {
        var contractResolver = new DefaultContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy(processDictionaryKeys: true, overrideSpecifiedNames: false),
        };

        var settings = new JsonSerializerSettings
        {
            ContractResolver = contractResolver,
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
        };

        settings.Converters.Add(new StringEnumConverter { NamingStrategy = contractResolver.NamingStrategy });
        return settings;
    }
}
