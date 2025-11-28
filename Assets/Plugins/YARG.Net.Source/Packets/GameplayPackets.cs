using System;
using System.Collections.Generic;

namespace YARG.Net.Packets;

public sealed record GameplayCountdownPacket(Guid SessionId, int SecondsRemaining) : IPacketPayload;

public sealed record GameplayInputFramePacket(Guid SessionId, long FrameNumber, IReadOnlyList<InputEvent> Inputs) : IPacketPayload;

public sealed record InputEvent(Guid PlayerId, string Input, double Value, long Timestamp);

/// <summary>
/// Sent by clients to broadcast their gameplay state to other players.
/// Replicated via server to all connected clients.
/// </summary>
public sealed record GameplayStatePacket(
    Guid SessionId,
    uint Sequence,
    int Score,
    int Combo,
    int Streak,
    bool StarPowerActive,
    float StarPowerAmount,
    int StarPowerPhrasesHit,
    int TotalStarPowerPhrases,
    int NotesHit,
    int NotesMissed,
    int Overstrums,
    int HoposStrummed,
    int Overhits,
    int GhostInputs,
    int GhostsHit,
    int AccentsHit,
    int DynamicsBonus,
    int BandBonusScore,
    int VocalsTicksHit,
    int VocalsTicksMissed,
    float VocalsPhraseTicksHit,
    int VocalsPhraseTicksTotal,
    bool SoloActive,
    int SoloSequence,
    int SoloNoteCount,
    int SoloNotesHit,
    int SoloLastBonus,
    int SoloTotalBonus,
    double SongTime,
    double ClientNetworkTime) : IPacketPayload;

/// <summary>
/// Sent by the host to signal gameplay is starting with synchronized timing.
/// </summary>
public sealed record GameplayStartPacket(
    Guid LobbyId,
    double ServerTime,
    double SongStartTime) : IPacketPayload;

/// <summary>
/// Sent to sync chart time across clients during gameplay.
/// </summary>
public sealed record GameplayTimeSyncPacket(
    Guid LobbyId,
    double ServerTime,
    double SongTime) : IPacketPayload;

/// <summary>
/// Sent when a player pauses or resumes gameplay.
/// </summary>
public sealed record GameplayPausePacket(
    Guid SessionId,
    bool IsPaused,
    double PauseTime) : IPacketPayload;

/// <summary>
/// Sent when gameplay ends (song complete, fail, or quit).
/// </summary>
public sealed record GameplayEndPacket(
    Guid LobbyId,
    GameplayEndReason Reason) : IPacketPayload;

public enum GameplayEndReason
{
    SongComplete,
    AllPlayersFailed,
    HostEnded,
    Disconnected,
}
