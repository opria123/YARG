# Online Multiplayer Refactor Plan

_Last updated: 2025-11-22_

## 1. Background

The current online branch (`online-multiplayer-STUN-p2p`) layers Mirror directly into Unity scenes. `YargNetworkManager`, transport setup, gameplay state replication, UI flow, and even STUN/UPnP utilities all live inside `Assets/Script` and depend on Unity-specific types. The dedicated-server experiment (see `Build/DedicatedServer`, `docker/DedicatedServer.Dockerfile`, and `.github/workflows/dedicated-server.yml`) builds the entire Unity project headlessly and still executes the same Mirror scene graph. This approach led to several pain points reported by the core team:

- **Tight coupling to Unity**: Lobby orchestration, session state, serialization, and gameplay rules sit inside `MonoBehaviour` scripts, preventing reuse.
- **Maintenance burden**: Mirror-specific callbacks (spawning, NetworkTime synchronization, connection lifecycle) are spread across menus, gameplay flows, and data models. Mirroring changes or hotfixes across the standalone server and the client is error-prone.
- **Navigation edge cases**: Because the game client is both the UI shell and the server host, exiting or reloading scenes resets Mirror and disconnects all peers. Complex guard code (`YargNetworkManager`, `LobbyRoomMenu`, etc.) attempts to mask this but introduces bugs.
- **Transport rigidity**: Mirror’s built-in transports (Telepathy/KCP/SimpleWeb/etc.) add weight and still require Unity behaviours, making them unattractive for a lightweight dedicated server.

## 2. Goals

1. **Extract networking logic into a reusable library** (similar in spirit to `YARG.Core`) that is Unity-agnostic and shareable between the standalone dedicated server and the embedded client host.
2. **Adopt LiteNetLib** as the transport/runtime foundation for its small footprint, mature UDP reliability, and `NetPacketProcessor`-driven serialization.
3. **First-class dedicated server**: Provide a console/server build that links only the networking/gameplay library plus minimal host glue. The Unity client should spin up an in-process server by referencing the same assemblies.
4. **Clean seams for gameplay data**: Clearly separate lobby negotiation, song queueing, gameplay commands, and spectator/state replication.
5. **Improve maintainability**: Unit-test critical networking code without entering Play Mode and ensure upgrades are limited to focused libraries.

## 3. Proposed Architecture

### 3.1 Solution Layout

Two coordinated repositories keep responsibilities clear:

```
YARG/ (Unity client repo)
├── YARG.Core/                (submodule, unchanged)
├── Assets/                   (Unity project)
└── Packages/manifest.json    (consumes YARG.Net package or local override)

YARG.Networking/ (new repo)
├── src/YARG.Net/             (netstandard2.1 class library)
│   ├── Abstractions/
│   ├── Protocol/
│   ├── Server/
│   └── Transport/
├── src/YARG.ServerHost/      (console app, dedicated server)
├── src/YARG.Introducer/      (introducer / relay service)
├── tests/                    (unit + integration suites)
└── build/                    (pack & publish scripts)
```

- `YARG.Net` targets `netstandard2.1` so it can be referenced by Unity (`2021.3` supports it) and by .NET 8 server hosts.
- `YARG.Game` depends on `YARG.Net` via a packaged distribution (see §3.6) while still allowing a path-based override for active networking work.

### 3.2 Library Responsibilities

| Area | `YARG.Net` Responsibility | Unity Client Responsibility |
| --- | --- | --- |
| Transport | Wrap LiteNetLib with `INetTransport` and event loop. Provide simulated adapters for tests. | Provide configuration (ports, NAT traversal flags) and tick the transport from the main thread.
| Session Model | Define lobby, playlist, chart selection, player readiness, gameplay timeline syncing. Includes deterministic tick scheduling. | Render UI, apply state to Scene objects, collect player inputs, translate into protocol commands.
| Serialization | Implement `INetSerializer` on top of `NetPacketProcessor` with versioning and compression knobs. | Register any Unity-only DTO extensions (e.g., cosmetic selections) via partials.
| Dedicated Server | Provide hosting lifecycle, command interface (start, stop, list lobbies), plugin hooks for metrics/logging. | Unity server build only bootstraps the same host when running in embedded mode.

#### Client bootstrap helper

The `ClientNetworkingBootstrapper.Initialize(transport, serializer)` helper wires up `DefaultClientRuntime`, a shared `ClientSessionContext`, `ClientLobbyStateHandler`, and the default command sender in one call. Consumers can subscribe to `IClientRuntime.HandshakeCompleted`, read `IClientRuntime.SessionContext`, and start issuing lobby commands without manually plumbing session IDs.

### 3.3 Transport Abstraction

- Introduce `INetTransport`, `INetConnection`, and `ITransportFactory` in `YARG.Net.Transport`.
- Provide an initial `LiteNetLibTransport` implementation that encapsulates `NetManager`, configures delivery channels (ReliableOrdered for commands, Sequenced for input snapshots, Unreliable for telemetry).
- Offer testing shims (`LoopbackTransport`) for unit tests.
- Keep the abstraction minimal: connect, disconnect, send (with enum `ChannelType`), event callbacks.

### 3.4 Protocol Layers

1. **Handshake**: exchange protocol version, password/token, instrumentation data.
2. **Lobby**: lobby settings, player roster, instrumentation (ping/region), locking/unlocking UI.
3. **Gameplay**: deterministic start time broadcast (`double serverTime` similar to `Mirror.NetworkTime`), periodic chart time sync, input frames, scoring events.
4. **Auxiliary**: chat, custom emotes, metadata fetch.

Each layer is defined via `record struct` DTOs plus associated handlers registered with LiteNetLib’s `NetPacketProcessor`.

### 3.5 Dedicated Server Modes

- **Standalone**: `YARG.ServerHost` runs with config YAML/CLI args. It loads setlists, charts, and charts metadata via `YARG.Core` to keep feature parity.
- **Embedded Host**: Unity client spins up an in-process `ServerRuntime` by referencing `YARG.Net.Server`. Networking still uses LiteNetLib, but loopback connections stay inside the process (client connects to `127.0.0.1:port`).
- **Docker**: `docker/DedicatedServer.Dockerfile` now bases on `mcr.microsoft.com/dotnet/runtime` and copies only the server binaries + content bundles, drastically shrinking image size.

### 3.6 Repository & Package Distribution

- **Primary dependency path**: `YARG.Networking` publishes `YARG.Net` as a versioned NuGet package (via GitHub Packages or another feed). `YARG`, `YARG.ServerHost`, and `YARG.Introducer` reference that package to stay in sync without cloning each other.
- **Local development override**: When iterating on networking code alongside the Unity client, we point `Packages/manifest.json` (or an `.asmdef` reference) to a local file path inside a sibling checkout of `YARG.Networking`. .NET projects can similarly swap the NuGet reference for a `<ProjectReference>` via conditional item groups.
- **Release workflow**: Every merge to `main` in `YARG.Networking` triggers `dotnet pack` + `dotnet nuget push`, tags the repo (e.g., `net-v0.2.0`), and updates a changelog. Client/server repos bump their dependency versions through normal PRs.
- **Emergency fixes**: If Unity or the server needs hotfixes before a package is published, we can temporarily consume a locally-built `.nupkg` from a file feed or use the path override; once CI publishes an official build, we revert the override.

## 4. Migration Strategy

| Phase | Deliverables | Notes |
| --- | --- | --- |
| 0. Freeze | Tag current Mirror branch; stop new feature work on Mirror path except critical fixes. | Ensures we can diff/regress if needed. |
| 1. Skeleton | Create `YARG.Net` project with stubs for transport, session, protocol, and `YARG.ServerHost`. Add CI (dotnet test/build). | No Unity dependencies yet; ensures solution builds outside Unity. |
| 2. Data Model Extraction | Move plain C# networking DTOs (e.g., `NetworkPlayerData` minus Unity types) into `YARG.Net`. Replace Unity-specific bits with injection points. | Keep Mirror running by referencing the new DTOs through adapter classes. |
| 3. Transport Swap | Implement LiteNetLib transport and integrate into the new server runtime. Provide a thin Unity component `LiteNetLibClientBehaviour` that wraps the transport for Play Mode. | During this phase, Mirror still powers shipping builds but we can start dual-running for testing. |
| 4. Gameplay Sync | Port the gameplay sync logic (ready checks, countdown, input frames) from Mirror callbacks into deterministic services. Write unit/integration tests using loopback transport. | Requires instrumenting `GameManager` to pull data from `YARG.Net` interfaces. |
| 5. UI Integration | Rebuild multiplayer menus to talk to the new service layer via async tasks instead of Mirror events. | Use dependency injection or ScriptableObjects to provide service references. |
| 6. Decommission Mirror | Remove Mirror packages, transports, and scripts once parity tests pass and regression QA is complete. | Update documentation & CI to stop building Mirror assets. |

## 5. LiteNetLib Adoption Details

- Add `LiteNetLib` via NuGet to `YARG.Net` and via `Packages/manifest.json` for Unity (using `scopedRegistries` or `Packages/packages-lock.json`).
- Configure channels:
  - `Channel.ReliableOrdered`: lobby commands, RPC-style updates.
  - `Channel.Sequenced`: input snapshots and chart synchronization frames.
  - `Channel.Unreliable`: telemetry (ping, presence, voice chat metadata later).
- Use `NetDataWriter`/`NetDataReader` for zero-GC serialization; wrap them behind `INetSerializer` so Unity UI can still log/inspect messages during debugging.
- Implement NAT traversal helpers: LiteNetLib already includes NAT punch-through support; reuse our existing STUN/UPnP helpers (`Assets/Script/Networking/StunUtility.cs`, `UPnP`) by calling them _before_ spinning up the transport, not from inside it.

## 6. Testing & Tooling

- **Unit Tests**: Add `YARG.Net.Tests` with deterministic simulations (loopback transport, deterministic random seeds) to validate handshake, lobby churn, and gameplay start.
- **Integration Harness**: Provide a simple CLI client (could live inside `YARG.ServerHost` project) that can join a lobby, start a song, and spam frames—useful for CI load tests.
- **CI**: Extend GitHub Actions to build/test `YARG.Net` and `YARG.ServerHost`. The dedicated-server workflow can switch to `dotnet publish` + `docker build` using the lean runtime image.

## 7. Next Steps Checklist

1. Create the `YARG.Networking` repository with skeleton projects (`YARG.Net`, `YARG.ServerHost`, `YARG.Introducer`) plus CI that runs `dotnet build`/`test`.
2. Add the NuGet packaging pipeline (GitHub Packages feed, semantic version tags) and document the Unity/local override workflow.
3. Define initial abstractions: `INetTransport`, `INetSerializer`, `IServerRuntime`, `IClientRuntime`.
4. Port shared DTOs from `NetworkPlayerData`, `LobbyInfoSync`, and `MultiplayerSongQueue` into the new library.
5. Implement the LiteNetLib transport adapter and basic loopback tests.
6. Flesh out `YARG.ServerHost` (config loader, CLI) and the minimal `YARG.Introducer` skeleton for lobby discovery.
7. Refactor Unity menus/gameplay to call into the service interfaces while Mirror still drives the low-level transport (adapter layer) to reduce risk.
8. Remove Mirror dependencies once feature parity and regression testing are complete.

Following this plan keeps networking logic isolated, makes the dedicated server a first-class citizen, and aligns with the core team’s request for a cleaner, maintainable architecture built on LiteNetLib.

## 8. Mirror Implementation Inventory (2025-02-27)

### 8.1 Scenes & Prefabs
- **`Assets/Scenes/PersistentScene.unity`**: contains the root `NetworkManager` GameObject with `KcpTransport`, `YargNetworkDiscovery`, `YargNetworkManager`, and the Mirror `NetworkManager` MonoBehaviour. Transport defaults (UDP 7777, fast resend, 60 Hz send rate) plus discovery handshake (`secretHandshake = -8828471100410706536`) are serialized here, so the LiteNetLib host needs equivalent bootstrap data.
- **`Assets/Scenes/MenuScene.unity`**: wires the online menus (`OnlineMultiplayerMenu`, `LobbyBrowserMenu`, `LobbyRoomMenu`, dialogs, sidebar) directly into the menu hierarchy. Any refactor must either keep those prefabs resident or spawn them via a new menu loader to avoid breaking navigation references baked into the scene.
- **Lobby browser prefabs** (`Assets/Prefabs/Menu/LobbyMenu/LobbyView.prefab`, `MiniPlayerView.prefab`): supply ListMenu row visuals, including localization-ready TMP labels, ping indicators, and bindings for favorites/discovery badges. Both depend on sprite GUIDs for lock/unlock/visibility icons.
- **Menu shell prefabs** (`OnlineMultiplayerMenu.prefab`, `LobbyRoomMenu.prefab`): expose serialized references for navigation groups, host-only buttons, address visibility toggles, and toast hooks. Scripts rely on these objects being active in MenuScene before pushing the menu stack.
- **Dialog prefabs** (`CreateLobbyDialog`, `DirectConnectDialog` under `Assets/Prefabs/Menu/LobbyMenu`): provide TMP input bindings and button wiring for lobby creation and direct connect flows. The dialogs assume the `Navigator` stack is suppressed while text inputs are focused.
- **Networked spawnables**: `MultiplayerShowPlaylist` is registered via `YargNetworkManager.RegisterMultiplayerShowPlaylistSpawnHandler()` with a fixed asset hash (`0x12345678`) and needs to stay in the `Spawnable Prefabs` list until LiteNetLib offers an equivalent spawn/replication path.

### 8.2 Core Networking Flow (Mirror-specific)
- **`YargNetworkManager`** (`Assets/Script/Networking/YargNetworkManager.cs`): central authority for lobby lifecycle, transport setup, NAT traversal (UPnP + STUN), lobby probing (`LobbyProbeRequest/Response` messages), player SyncVar management, and shared song library reconciliation. It also owns events consumed across the UI (`OnLobbyListUpdated`, `OnPlayerJoined`, `OnSharedSongSyncStateChanged`, etc.) and handles special cases for dedicated servers vs. listen hosts.
- **Discovery & bookmarks**: `YargNetworkDiscovery` (serialized in `PersistentScene.unity`) feeds `LobbyBrowserMenu` along with manual ping results. Bookmark persistence lives in `YARG.Networking.Bookmarks` (used heavily by `LobbyFavorites`, `LobbyBookmarkStore`, and the saved lobby view types).
- **Endpoint helpers**: `Assets/Script/Networking/EndpointUtility.cs` plus `NetworkTransportDefaults` wrap parsing, formatting, and default-port resolution. `NetworkTransportDefaults.DefaultUdpPort` now defers to the new settings entry, so the LiteNetLib transport has to honor `SettingsManager.Instance.NetworkPort` as well.
- **Player data**: `NetworkPlayerData` (not shown here but consumed by `MultiplayerPlayerManager`, `MultiplayerDifficultySync`, and `MultiplayerGameplaySync`) exposes Commands for ready states, instrument selections, and playlist actions. Any future transport must keep compatible serialized fields for profile snapshots, ready booleans, and gameplay snapshots.
- **Shared playlist replication**: `MultiplayerShowPlaylist` SyncVar-serializes the show order (`songHash` pipe-separated string), fires toast RPCs, and drives menu navigation on both host and clients. Migration needs an equivalent broadcast so `GlobalVariables.State.ShowSongs` stays in sync.
- **Song library sync**: `YargNetworkManager` tracks `_playerSongLibraries`, `HashWrapper` sets, and `_playersPendingSongSync` to ensure lobby hosts know which charts every client can play. None of this logic lives in the UI, so LiteNetLib must preserve the same chunked transfer semantics before we remove Mirror.

### 8.3 Menu & UX Scripts
| Script | Responsibility | Notes & Dependencies |
| --- | --- | --- |
| `OnlineMultiplayerMenu.cs` | Entry point that pushes the lobby browser, opens dialogs, and surfaces connection status (`Menu.LobbyBrowser.Status*` keys). | Depends on `Navigator`, `DialogManager`, and assumes localized strings exist (currently missing in `en-US`, `es-ES`, `ja-JP`). |
| `LobbyBrowserMenu.cs` | ListMenu-based browser with favorites, recents, LAN discovery, direct pinging, and password caching. | Heavy use of `YargNetworkManager` events, `YargNetworkDiscovery`, `LobbyFavorites`, and `LobbyViewType` subclasses under `Assets/Script/Menu/Multiplayer/ViewTypes`. |
| `LobbyBrowserSidebar.cs` | Hosts Create Lobby / Direct Connect panels, shows lobby details, and emits button events consumed by the menu. | References sprite toggles for visibility icons and uses localization keys such as `Menu.LobbyBrowser.PasswordSaved`. |
| `LobbyFavorites.cs` / `LobbyBookmarkStore` | Persists favorite/Recent servers, handles password saving, and bridges to `LobbyBookmarkUtility`. | Writes to disk via `Application.persistentDataPath`; ensure migrations keep file schema intact. |
| `LobbyView.cs` / `MiniPlayerView.cs` | Visual representation of lobbies and player rows, including hover state, ping icons, host badges, and fallback text mapping. | Relies on TMP hierarchies defined in prefabs plus sprites from `Assets/Art/Menu/Common/Icons/*.png`. |
| `CreateLobbyDialog.cs` / `DirectConnectDialog.cs` | Collect lobby name, player cap, privacy/password, and direct IP endpoints; call into `YargNetworkManager.HostLobby` and `.JoinLobby`. | Use `EndpointUtility`, `DialogManager`, and localization keys (`ConnectingTitle`, `InvalidEndpointTitle`, etc.). |

### 8.4 Gameplay & Playlist Sync
| Component | Purpose | Key Details |
| --- | --- | --- |
| `SongQueueSystem.cs` | **Removed** legacy MonoBehaviour queue stub. Functionality now lives in `MultiplayerShowPlaylist` + UI bindings. | Kept in git history only; new queue UI listens directly to `MultiplayerShowPlaylist` events. |
| `MultiplayerShowPlaylist.cs` | Mirror NetworkBehaviour that SyncVar-serializes the shared show playlist, handles Commands/RPCs for add/remove/start/clear. | Updates `GlobalVariables.State.ShowSongs`, fires toast messages via `Menu.Persistent.ToastManager`, and navigates clients to Difficulty Select. |
| `MultiplayerMusicLibrary.cs` | Adds multiplayer affordances to the music library (selected song label, host-only start button). | Subscribes to `YargNetworkManager.OnSongSelected` and calls `StartMultiplayerSong`. |
| `MultiplayerPlayerManager.cs` | Builds per-session `YargPlayer` instances from `NetworkPlayerData`, including temp profiles and binding updates. | Used by `GameManager.Loading` to seed gameplay; must exist before chart load when running headless hosts. |
| `MultiplayerDifficultySync.cs` | Syncs difficulty/instrument choices, requests ready state, and calls `StartMultiplayerGameplay` when all players are prepared. | Hooks into `YargNetworkManager` events, Mirror Commands on `NetworkPlayerData`, and exposes `OnWaitingForPlayers` for UI prompts. |
| `MultiplayerGameplaySync.cs` | Captures gameplay snapshots (score, combo, SP, vocals ticks, etc.) and replicates them across the network at ~50 Hz when state changes. | Adds sequencing & deadband constants; dependent on Mirror RPCs today, so transport replacement must offer equivalent frequency and payload sizes. |

### 8.5 Assets, Addressables & Sprites
- **Addressables**: `Assets/Settings/AddressableAssetsData/AssetGroups/Default Local Group.asset` now lists `AssortedIcons`, `CloseIcon`, `NoInstrumentIcons`, and the `DifficultySprites` TMP atlas. Any bundle rebuild for LiteNetLib testing needs these entries preserved so the lobby UI can load icons via their address (`CloseIcon`, etc.).
- **Menu icons** (`Assets/Art/Menu/Common/Icons`): new/updated textures include `close.png`, `Visible.png`, `invisible.png`, `host.png`, `LockIcon.png`, `UnlockIcon.png`, `WifiIcon.png`, and `group.png`. Prefabs reference their GUIDs directly (e.g., `invisible.png` GUID `4b954e4eb67b66b4688a3b085b1fecf1`), so GUID stability matters when copying assets into another repo.
- **Font assets**: `Assets/Art/Fonts/FontSprites.asset` gained the `group` glyph, while `DifficultySprites.asset` is referenced by addressables for fallback scoreboard sprites. Ensure the new glyph indices are kept if TMPro assets are regenerated.
- **Localization-ready TMP**: `LobbyView` actively looks for named TMP children (e.g., "Lobby Name", "Ping Text"). Any prefab restructure must keep these names or update the reflection logic.

### 8.6 Localization & Settings
- **Lobby Browser strings**: `en-US.json` contains the long-form lobby browser keys but is missing the new connection status entries (`StatusHosting`, `StatusConnected`, `StatusNotConnected`) that exist in `pt-BR`/`pt-PT`. `es-ES` and `ja-JP` still have the pre-refactor minimal set (no ping/privacy/help text, no password messages). A LiteNetLib cutover must budget time to finish these translations (keys live under `Menu.LobbyBrowser`).
- **Scoreboard readiness**: `Menu.ScoreScreen.Ready*` strings already exist in all locales and are consumed by `MultiplayerGameplaySync`/scoreboard UI; keep them if the ready-up flow moves to the new transport.
- **New setting**: `Settings.NetworkPort` exists in all language files (name + description) and in `SettingsManager.Settings.cs`. The UI writes to `SettingsMenu` via `nameof(Settings.NetworkPort)`, so the LiteNetLib transport should read the same value before binding sockets.
- **Dialogs**: Strings like `ConnectingTitle`, `ConnectingDescription`, `InvalidEndpointTitle`, `PasswordRequiredMessage`, and `Menu.LobbyBrowser.PasswordSavedStatus` are scattered through the dialogs/sidebar. Ensure the localization namespace stays `("Menu","LobbyBrowser",*)` when porting.
- **Language coverage snapshot**:

| Locale | Lobby browser coverage | Notes |
| --- | --- | --- |
| `en-US` | Most strings present, but connection status keys missing. | Needs `StatusHosting/Connected/NotConnected` before release. |
| `es-ES`, `ja-JP` | Only legacy subset (no ping/privacy/favorites messaging). | Require full translation pass. |
| `pt-BR`, `pt-PT` | Complete set including favorites, toast text, status, and host prompts. | Can be reference for other translators. |

### 8.7 Dedicated Server & Headless Support
- **Dockerfile** (`docker/DedicatedServer.Dockerfile`): Ubuntu 22.04 base installs minimal X11 deps, exposes both TCP/UDP 7777, sets env vars (`YARG_MAX_PLAYERS`, `YARG_PRIVACY`, `YARG_PASSWORD`, `YARG_LOBBY_NAME`, `YARG_HOST_NAME`, `YARG_DEDICATED`, `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT`) before launching `./YARGServer -batchmode -nographics -dedicated`. Docker image assumes `Build/DedicatedServer` already exists, so CI must keep producing that folder.
- **Headless audio**: `Assets/Script/Audio/Headless/NullAudioManager.cs` is a no-op `AudioManager` for dedicated servers. When LiteNetLib replaces Mirror, keep this type so Unity headless builds remain stable.
- **Menu guardrails**: `GameManager.Loading` now waits for `MultiplayerPlayerManager.CreateMultiplayerPlayers()` before spawning lanes, logging verbose debug output. Dedicated servers skip UI navigation inside `MultiplayerShowPlaylist.CmdStartShow()` and rely solely on state transitions.

### 8.8 Outstanding Parity / Risks for LiteNetLib Migration
- Localization debt (missing lobby strings outside Portuguese) will block UX parity. Capture outstanding keys now so translators can work in parallel with transport changes.
- `SongQueueSystem` has been deleted. Queue UI now consumes `MultiplayerShowPlaylist` state/events directly, so there is no lingering local-only queue state to port.
- `MultiplayerMusicLibrary` depends on `YargNetworkManager.OnSongSelected`, which is currently not dispatched anywhere. Verify whether that event is still reached; otherwise the new transport should formalize a "song proposed" message.
- Bookmark persistence and password saving rely on the existing JSON schema inside `YARG.Networking.Bookmarks`. When introducing a new net lib, keep that schema or provide a migration strategy to avoid losing user data.
- The dedicated server Dockerfile expects the Unity headless build; once networking moves out of Unity, document how the new server binary replaces `./YARGServer` (or update the Docker instructions accordingly).

## 9. Queue Integration Plan (Fold SongQueueSystem into MultiplayerShowPlaylist)

### 9.1 Usage Audit (2025-11-22)
- `SongQueueSystem` has been removed entirely. No scripts referenced it, so the delete was a no-op for runtime behavior.
- Implication: all queue-facing UI must talk to `MultiplayerShowPlaylist` (or future transport abstraction) for show state.

### 9.2 Proposed `MultiplayerShowPlaylist` Surface
| Category | New API | Purpose |
| --- | --- | --- |
| Events | `event Action OnQueueChanged` | Raised whenever the serialized playlist payload changes (add/remove/reorder/clear). Mirrors the existing SyncVar hook but gives menus a clean subscription point without parsing `GlobalVariables.State` themselves. |
| | `event Action<PlaylistSongInfo> OnSongAdded` | Provides per-song context (hash, resolved metadata, queued-by player) so Toast/UI rows can update immediately. |
| | `event Action<int> OnSongRemoved` | Lets UI trim specific rows without rebuilding from scratch. |
| | `event Action OnSetStarted` / `event Action OnSetEnded` | Replaces `SongQueueSystem`’s set lifecycle notifications; driven when host calls `CmdStartShow` and when the playlist clears/finishes. |
| | `event Action<SongEntry> OnSongStarting` | Fired before navigating to Difficulty Select so countdown/preview widgets can react. |
| State accessors | `IReadOnlyList<SongEntry> CurrentQueue` | Returns the locally deserialized playlist (already tracked via `_localShowPlaylist`). |
| | `bool IsPlayingSet` / `int CurrentSongIndex` | Matches `SongQueueSystem` fields so UI can highlight the active song. |
| Queue actions | `bool TryAddSong(SongEntry song, string playerName)` | Host/client helper that wraps `CmdAddSongToShow` with metadata extraction, de-dupe, and toast emission. |
| | `bool TryRemoveSong(HashWrapper songHash, string playerName)` | Wraps `CmdRemoveSongFromShow`. |
| | `bool TryReorderSong(int fromIndex, int toIndex)` (host-only) | Optional parity with `SongQueueSystem.ReorderSong`. |
| | `void ClearQueue(NetworkConnectionToClient sender)` | Calls `CmdClearShowPlaylist`. |

The existing SyncVar hook (`OnShowPlaylistChanged`) already refreshes `_localShowPlaylist` on every client, so these APIs can reuse that data rather than duplicating storage.

`MultiplayerShowPlaylist` now ships a lightweight `PlaylistSongInfo` DTO (`Assets/Script/Multiplayer/PlaylistSongInfo.cs`) that bundles the hash, resolved metadata, queued-by player, optional `SongEntry`, and timestamp for UI consumers.

### 9.3 Migration Steps
1. ✅ **Add events + accessors** to `MultiplayerShowPlaylist`. `OnQueueChanged`, `OnSongAdded/Removed`, `OnSetStarted/Ended`, `OnSongStarting`, and queue snapshots now raise on both host and clients.
2. ✅ **Wire `MultiplayerMusicLibrary` + Music Library UI** to the playlist surface. `MusicLibraryMenu`/`SongViewType` call `TryAddSong`/`TryRemoveSong`, while `MultiplayerMusicLibrary` listens to the new events to show queue state and trigger `CmdStartShow` instead of `YargNetworkManager.StartMultiplayerSong`.
3. ✅ **Delete `SongQueueSystem`**; no prefabs referenced it, so removing the script + meta was clean.
4. ✅ **Document the new API** here (see `PlaylistSongInfo` blurb above) so LiteNetLib can mirror the same signals without crawling old Mirror-specific code.

## 10. Unity Client Integration Plan

### 10.1 Ship `YARG.Net` into the Unity workspace
- Add a `NewtonsoftNetSerializer` implementation to `YARG.Net.Serialization` so Unity can rely on the built-in `com.unity.nuget.newtonsoft-json` package instead of bundling `System.Text.Json`.
- Introduce a `ClientHandshakeRequestSender` helper (plus tests) that emits `HandshakeRequestPacket` payloads once the transport connects. This keeps handshake logic outside Unity code and lets both the console client and Unity reuse the same helper.
- Build `YARG.Networking/src/YARG.Net` in Release mode and copy the output (`YARG.Net.dll`, `YARG.Net.xml`, `LiteNetLib.dll`, `Newtonsoft.Json.dll`, `Microsoft.Bcl.AsyncInterfaces.dll`, and the `System.*` dependencies) directly into `YARG/Assets/Plugins/YARG.Net/`. During local development we reference these loose files instead of publishing a package so changes can be tested immediately without bumping package versions.
- Document the copy command (see `Assets/Plugins/YARG.Net/README.md`) so future updates stay in sync with the library repo.
- Confirm `Assembly-CSharp.csproj` (and Rider/VS) can resolve the assemblies without manual references by storing them under `Assets/Plugins`.

### 10.2 Create a Unity bootstrapper
- Add `ClientNetworkingService` (pure C# singleton) under `Assets/Script/Networking/NewNet/`. It should wrap the `ClientNetworkingBootstrapper.Initialize()` return object, expose `ConnectAsync`, `DisconnectAsync`, `SendReadyState()`, `SendSongSelection()`, and surface the cached `ClientSessionContext`, `ClientLobbyStateHandler`, and `ClientLobbyCommandSender` objects.
- Add `ClientNetworkingBehaviour : MonoBehaviour` that lives in `PersistentScene`. It sets up the service on `Awake`, drives `Application.runInBackground`, and pumps a simple `Update()` hook until `DefaultClientRuntime` owns full polling. Keep it `DontDestroyOnLoad` so all menus can access `ClientNetworkingService.Instance` without scene reloads.
- Define Unity events/actions for `HandshakeCompleted`, `LobbyStateChanged`, and transport errors so menu scripts can subscribe without touching `YARG.Net` types directly.
- Persist lightweight config (desired port, player name, last password) in `NetworkConfig` so the new behaviour matches what `YargNetworkManager` used to do.

### 10.3 Migrate menu flows incrementally
- **OnlineMultiplayerMenu**: Replace `YargNetworkManager` access with the new service. Connection status text should key off `ClientNetworkingService.State`. On Find/Create/Direct Connect, call the service instead of Mirror.
- **LobbyBrowserMenu**: Consume `ClientLobbyStateHandler.LobbyStateChanged` snapshots to hydrate lobby rows, direct-connect to introducer/discovery results, and forward join attempts through the new transport (Mirror discovery code can stay until the introducer/API lands).
- **LobbyRoomMenu & Multiplayer ready flow**: Toggle ready state by calling `ClientLobbyCommandSender.SendReadyState()` and listen for `LobbyStateSnapshot.Selection` updates to drive the lobby UI.
- **MultiplayerShowPlaylist & gameplay entry**: Convert playlist actions to the new command sender and rely on `ClientSessionContext` IDs instead of Mirror connection IDs. Use the new service for countdown + session tracking before touching gameplay scenes.
- Keep `YargNetworkManager` alive until each menu/script has a feature-complete alternative. Gate the new path behind an editor-only flag during development to avoid breaking existing builds mid-migration.

### 10.4 Decommission Mirror pieces
- Once the menus and gameplay flows run end-to-end through the new service, strip the `YargNetworkManager` GameObject from `PersistentScene.unity`, remove Mirror packages, and delete unused scripts (UPnP, discovery, password authenticators) that are superseded by the LiteNetLib path.
- Update documentation (`README`, `docs/online-multiplayer-refactor.md`, and `DESIGNING.md`) plus the dedicated server workflow to reference the new client bootstrapper instead of Mirror.
- Add regression tests (play mode or integration harness) confirming that the Unity client can connect to `YARG.ServerHost`, complete the handshake, toggle ready, and receive lobby snapshots, then green-light the Mirror removal milestone.
