using YARG.Networking.Abstraction;

namespace YARG.Gameplay.Player
{
    /// <summary>
    /// Interface for remote player simulation components.
    /// Allows BasePlayer to communicate with network simulation without direct coupling.
    /// </summary>
    public interface IRemotePlayerSimulation
    {
        /// <summary>
        /// The network player data associated with this simulation.
        /// </summary>
        NetworkPlayerData NetworkPlayerData { get; }

        /// <summary>
        /// Whether the simulation is currently active.
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// Initializes the simulation with the player and network data.
        /// </summary>
        void Initialize(BasePlayer player, NetworkPlayerData networkPlayerData);

        /// <summary>
        /// Applies a network state update to the simulation.
        /// </summary>
        void ApplyNetworkState(int score, int combo, bool isStarPowerActive, float starPowerAmount);

        /// <summary>
        /// Applies remote state at the given time (called during engine update).
        /// </summary>
        void ApplyRemoteState(double time);
    }
}
