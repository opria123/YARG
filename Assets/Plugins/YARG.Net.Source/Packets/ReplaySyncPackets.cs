using System;

namespace YARG.Net.Packets;

/// <summary>
/// Sent by server to all clients to request their replay data after gameplay ends.
/// </summary>
public sealed record ReplaySyncRequestPacket(
    Guid LobbyId) : IPacketPayload;

/// <summary>
/// Sent by client to server with their serialized replay frame after gameplay ends.
/// </summary>
public sealed record ReplaySyncDataPacket(
    Guid SessionId,
    byte[] SerializedReplayFrame,
    byte[] SerializedReplayStats,
    Guid ColorProfileId,
    string ColorProfileJson,
    Guid CameraPresetId,
    string CameraPresetJson,
    double[] FrameTimes) : IPacketPayload;

/// <summary>
/// Sent by server to all clients when replay sync is complete.
/// </summary>
public sealed record ReplaySyncCompletePacket(
    Guid LobbyId) : IPacketPayload;
