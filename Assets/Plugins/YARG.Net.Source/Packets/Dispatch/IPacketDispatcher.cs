using System;
using System.Threading;
using System.Threading.Tasks;

namespace YARG.Net.Packets.Dispatch;

public interface IPacketDispatcher
{
    void RegisterHandler<TPayload>(PacketType type, PacketHandler<TPayload> handler)
        where TPayload : IPacketPayload;

    bool TryUnregisterHandler(PacketType type);

    Task<bool> DispatchAsync(ReadOnlyMemory<byte> payload, PacketContext context, CancellationToken cancellationToken = default);
}
