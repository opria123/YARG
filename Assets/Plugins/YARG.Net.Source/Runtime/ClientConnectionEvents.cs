using System;
using YARG.Net.Transport;

namespace YARG.Net.Runtime;

public sealed class ClientConnectedEventArgs : EventArgs
{
    public ClientConnectedEventArgs(INetConnection connection)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public INetConnection Connection { get; }
}

public sealed class ClientDisconnectedEventArgs : EventArgs
{
    public ClientDisconnectedEventArgs(INetConnection connection, bool initiatedDuringConnect)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        InitiatedDuringConnect = initiatedDuringConnect;
    }

    public INetConnection Connection { get; }
    public bool InitiatedDuringConnect { get; }
}
