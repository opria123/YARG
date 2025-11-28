using System.Threading;
using System.Threading.Tasks;

namespace YARG.Net.Packets.Dispatch;

public delegate Task PacketHandler<TPayload>(PacketContext context, PacketEnvelope<TPayload> envelope, CancellationToken cancellationToken)
    where TPayload : IPacketPayload;
