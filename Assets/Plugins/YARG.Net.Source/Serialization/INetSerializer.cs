using System;

namespace YARG.Net.Serialization;

public interface INetSerializer
{
    ReadOnlyMemory<byte> Serialize<T>(T payload);
    T Deserialize<T>(ReadOnlySpan<byte> payload);
}
