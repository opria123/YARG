using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YARG.Net.Serialization;

namespace YARG.Net.Packets.Dispatch;

/// <summary>
/// Routes serialized packets to registered handlers using <see cref="INetSerializer"/>.
/// </summary>
public sealed class PacketDispatcher : IPacketDispatcher
{
    private readonly INetSerializer _serializer;
    private readonly ConcurrentDictionary<PacketType, Func<ReadOnlyMemory<byte>, PacketContext, CancellationToken, Task>> _handlers = new();

    public PacketDispatcher(INetSerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    public void RegisterHandler<TPayload>(PacketType type, PacketHandler<TPayload> handler)
        where TPayload : IPacketPayload
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        var registered = _handlers.TryAdd(type, async (payload, context, cancellationToken) =>
        {
            var envelope = _serializer.Deserialize<PacketEnvelope<TPayload>>(payload.Span);
            await handler(context, envelope, cancellationToken).ConfigureAwait(false);
        });

        if (!registered)
        {
            throw new InvalidOperationException($"Handler already registered for packet type {type}.");
        }
    }

    public bool TryUnregisterHandler(PacketType type)
    {
        return _handlers.TryRemove(type, out _);
    }

    public async Task<bool> DispatchAsync(ReadOnlyMemory<byte> payload, PacketContext context, CancellationToken cancellationToken = default)
    {
        var packetType = ResolvePacketType(payload);
        if (!_handlers.TryGetValue(packetType, out var handler))
        {
            return false;
        }

        await handler(payload, context, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static PacketType ResolvePacketType(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("type", out var typeProperty))
            {
                throw new JsonException("Packet envelope missing 'type' property.");
            }

            if (typeProperty.ValueKind == JsonValueKind.String)
            {
                var typeString = typeProperty.GetString();
                if (Enum.TryParse<PacketType>(typeString, ignoreCase: true, out var parsed))
                {
                    return parsed;
                }
            }
            else if (typeProperty.ValueKind == JsonValueKind.Number && typeProperty.TryGetInt32(out var numeric))
            {
                return (PacketType)numeric;
            }

            throw new JsonException("Unable to resolve packet type from envelope.");
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new JsonException("Failed to parse packet envelope for dispatch.", ex);
        }
    }
}
