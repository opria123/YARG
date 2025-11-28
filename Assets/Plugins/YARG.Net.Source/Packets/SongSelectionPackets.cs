using System;
using System.Collections.Generic;

namespace YARG.Net.Packets;

public sealed record SongSelectionPacket(Guid SessionId, SongSelectionState State) : IPacketPayload;

public sealed record SongSelectionState(string SongId, IReadOnlyList<SongInstrumentAssignment> Assignments, bool AllReady);

public sealed record SongInstrumentAssignment(Guid PlayerId, string Instrument, string Difficulty);
