using System;
using YARG.Net.Transport;

namespace YARG.Net.Sessions;

/// <summary>
/// Represents a logical player session tracked by the server runtime.
/// </summary>
public sealed record SessionRecord(Guid SessionId, Guid ConnectionId, string PlayerName, DateTimeOffset CreatedAt, INetConnection Connection);
