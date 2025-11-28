using System;
using YARG.Net.Handlers.Client;
using YARG.Net.Packets.Dispatch;
using YARG.Net.Serialization;
using YARG.Net.Transport;

namespace YARG.Net.Runtime;

/// <summary>
/// Helper for wiring up the default client runtime, packet dispatcher, and session-aware helpers.
/// </summary>
public static class ClientNetworkingBootstrapper
{
    public static ClientNetworkingClient Initialize(INetTransport transport, INetSerializer serializer, TimeSpan? pollInterval = null)
    {
        if (transport is null)
        {
            throw new ArgumentNullException(nameof(transport));
        }

        if (serializer is null)
        {
            throw new ArgumentNullException(nameof(serializer));
        }

        var runtime = new DefaultClientRuntime(pollInterval);
        runtime.RegisterTransport(transport);

        var dispatcher = new PacketDispatcher(serializer);
        runtime.RegisterPacketDispatcher(dispatcher);

        var sessionContext = new ClientSessionContext();
        runtime.RegisterSessionContext(sessionContext);

        var lobbyStateHandler = new ClientLobbyStateHandler();
        lobbyStateHandler.Register(dispatcher);

        var countdownHandler = new ClientCountdownHandler();
        countdownHandler.Register(dispatcher);

        var gameplayHandler = new ClientGameplayHandler(serializer);
        gameplayHandler.Register(dispatcher);

        var commandSender = new ClientLobbyCommandSender(serializer);
        var handshakeSender = new ClientHandshakeRequestSender(serializer);

        return new ClientNetworkingClient(runtime, sessionContext, dispatcher, lobbyStateHandler, countdownHandler, gameplayHandler, commandSender, handshakeSender);
    }
}

public sealed record ClientNetworkingClient(
    IClientRuntime Runtime,
    ClientSessionContext SessionContext,
    IPacketDispatcher PacketDispatcher,
    ClientLobbyStateHandler LobbyStateHandler,
    ClientCountdownHandler CountdownHandler,
    ClientGameplayHandler GameplayHandler,
    ClientLobbyCommandSender CommandSender,
    ClientHandshakeRequestSender HandshakeSender);
