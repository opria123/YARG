using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace YARG.Networking.Tracks
{
    /// <summary>
    /// Network message for synchronizing track order from host to clients.
    /// </summary>
    public struct TrackOrderMessage : INetSerializable
    {
        /// <summary>
        /// The ordered list of player IDs.
        /// </summary>
        public List<Guid> PlayerOrder;

        /// <summary>
        /// Whether this is a custom order set by host.
        /// </summary>
        public bool IsCustomOrder;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(IsCustomOrder);
            writer.Put(PlayerOrder?.Count ?? 0);

            if (PlayerOrder != null)
            {
                foreach (var id in PlayerOrder)
                {
                    writer.Put(id.ToString());
                }
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            IsCustomOrder = reader.GetBool();
            int count = reader.GetInt();

            PlayerOrder = new List<Guid>(count);
            for (int i = 0; i < count; i++)
            {
                PlayerOrder.Add(Guid.Parse(reader.GetString()));
            }
        }
    }

    /// <summary>
    /// Request from client to host to reorder their track (if allowed).
    /// </summary>
    public struct TrackReorderRequestMessage : INetSerializable
    {
        /// <summary>
        /// The player requesting the reorder.
        /// </summary>
        public Guid PlayerId;

        /// <summary>
        /// The desired position (0-based index).
        /// </summary>
        public int DesiredPosition;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PlayerId.ToString());
            writer.Put(DesiredPosition);
        }

        public void Deserialize(NetDataReader reader)
        {
            PlayerId = Guid.Parse(reader.GetString());
            DesiredPosition = reader.GetInt();
        }
    }
}
