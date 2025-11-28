namespace YARG.Net.Transport;

/// <summary>
/// Logical channels map onto transport-specific delivery guarantees.
/// </summary>
public enum ChannelType
{
    ReliableOrdered = 0,
    ReliableSequenced = 1,
    Unreliable = 2,
}
