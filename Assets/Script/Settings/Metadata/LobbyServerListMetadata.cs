namespace YARG.Settings.Metadata
{
    /// <summary>
    /// Metadata for rendering an inline lobby server list within a settings tab.
    /// </summary>
    public class LobbyServerListMetadata : AbstractMetadata
    {
        public override string[] UnlocalizedSearchNames => new[] { "Lobby Server", "Server", "Multiplayer" };
    }
}
