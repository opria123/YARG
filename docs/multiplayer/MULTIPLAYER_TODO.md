# Multiplayer Implementation TODO

Last updated: December 31, 2025

---

## ✅ Completed

### UI Integration
| Task | Completed |
|------|-----------|
| **Session restrictions display** (game mode icons, No Fail, etc.) | ✅ Dec 16, 2025 |
| **Settings sync to clients** | ✅ Dec 16, 2025 |
| **Lobby browser sidebar** (settings preview before joining) | ✅ Dec 16, 2025 |
| **Discovery protocol fixes** (ping display, refresh, deduplication) | ✅ Dec 16, 2025 |
| **Lobby Servers management** settings menu | ✅ Dec 16, 2025 |
| **Bookmark editing UI** | ✅ Dec 16, 2025 |
| **Track reordering controls** in lobby | ✅ Dec 16, 2025 |
| **Band display** in lobby UI | ✅ Dec 30, 2025 |
| **Lobby creation wizard** | ✅ Dec 26, 2025 |
| **Join by Code dialog** | ✅ Dec 26, 2025 |

### Phase Integration (Backend + UI)
| Feature | Status |
|---------|--------|
| UPnP | ✅ Done |
| Band System | ✅ Done |
| Track Ordering | ✅ Done |
| Session Types | ✅ Done |
| Gameplay Settings | ✅ Done |

### Band System UI (✅ Completed Dec 30, 2025)
| Task | Completed |
|------|-----------|
| `BandHeaderView` prefab - displays band name with regenerate button | ✅ Dec 27, 2025 |
| `LobbyRoomMenu.RefreshPlayerList()` - group players by band | ✅ Dec 27, 2025 |
| Network sync for band names and assignments | ✅ Dec 27, 2025 |
| Client can request band name regeneration from host | ✅ Dec 27, 2025 |
| "Move to Band" dropdown on PlayerView (host only) | ✅ Dec 27, 2025 |
| Band validation error banner (toast notification) | ✅ Dec 27, 2025 |
| Band score aggregation via `BandScoreUpdate` packet | ✅ Dec 30, 2025 |
| `BandLeaderboardHUD` racing-style display | ✅ Dec 30, 2025 |
| Band-based star power/overdrive coordination | ✅ Dec 30, 2025 |
| Filter `MultiplayerPlayerManager` by band | ✅ Dec 30, 2025 |
| Per-band unison calculation | ✅ Dec 30, 2025 |
| End-of-song band results UI (`BandResultsPanel`, `BandResultEntry`) | ✅ Dec 30, 2025 |

### Bugs / Issues (✅ All Fixed Dec 31, 2025)
| Task | Fixed |
|------|-------|
| Lobby browser shows connection count instead of profile count | ✅ Dec 30, 2025 |
| Blocked game modes can still join/host | ✅ Dec 30, 2025 |
| Setlist instrument filtering too restrictive (Online only) | ✅ Dec 30, 2025 |
| Sit out shows wrong instrument after switching between songs | ✅ Dec 30, 2025 |
| Band score not syncing to host in band mode | ✅ Dec 30, 2025 |
| Multiple local profiles not added online | ✅ Dec 16, 2025 |
| Client back button at music library | ✅ Dec 22, 2025 |
| Solo score not showing for remote players | ✅ Dec 22, 2025 |
| Online song load time too slow | ✅ Dec 22, 2025 |
| Replay fail popup breaks score screen | ✅ Dec 22, 2025 |
| Client sent to main menu when host backs from library | ✅ Dec 22, 2025 |
| Star power resurrection for dead players | ✅ Dec 22, 2025 |
| Settings panel disappears after Music Library | ✅ Dec 22, 2025 |
| NoFail mode not preventing fails after toggle | ✅ Dec 22, 2025 |
| Fail screen shows wrong options in multiplayer | ✅ Dec 22, 2025 |
| Authentication error not showing reason to user | ✅ Dec 31, 2025 |
| Band standings star view showing 0 stars | ✅ Dec 31, 2025 |

---

## ✅ Completed

### Dedicated Server (Phases 1-5 Complete ✅)

#### Design Decisions (Dec 31, 2025)

| Decision | Choice | Rationale |
|----------|--------|-----------|
| **Configuration Source** | JSON file only | Simpler, portable, version-controllable |
| **Ready-up Timeout Behavior** | Force kick | Clear consequences for AFK players |
| **Host Assignment** | First player auto-host | Natural flow, admin can override |
| **Song Library** | Connected players only | Server doesn't need songs installed |
| **Admin Interface** | Embedded web UI | Single binary, no separate deployment |
| **Host Permissions (Dedicated)** | Song selection only | Prevents random players from griefing |
| **Admin Permissions** | Full control | Settings, kicks, host promotion |

#### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                    YARG Dedicated Server                        │
├─────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐ │
│  │  Game Server    │  │  Admin Web UI   │  │  Config Loader  │ │
│  │  (Port 9050)    │  │  (Port 8080)    │  │  (JSON file)    │ │
│  │                 │  │                 │  │                 │ │
│  │  • LiteNetLib   │  │  • HttpListener │  │  • Load on      │ │
│  │  • Player sync  │  │  • REST API     │  │    startup      │ │
│  │  • Gameplay     │  │  • Auth login   │  │  • Hot reload   │ │
│  │  • Discovery    │  │  • Dashboard    │  │    (optional)   │ │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘ │
│                              │                                  │
│                              ▼                                  │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │                   DedicatedServerManager                    ││
│  │  • Host promotion logic                                     ││
│  │  • Idle timeout tracking                                    ││
│  │  • Ready-up timeout enforcement                             ││
│  │  • Vote-to-kick coordination                                ││
│  │  • Admin command execution                                  ││
│  └─────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
```

#### Role Separation: Host vs Admin

| Action | Host (Player) | Admin (Web UI) |
|--------|---------------|----------------|
| Select songs | ✅ | ✅ |
| Ready up / start song | ✅ | ✅ |
| Change session settings | ❌ | ✅ |
| Kick players | ❌ | ✅ |
| Ban players | ❌ | ✅ |
| Promote new host | ❌ | ✅ |
| View player list | ✅ | ✅ |
| Move players between bands | ❌ | ✅ |
| Change band settings | ❌ | ✅ |
| Shutdown server | ❌ | ✅ |

#### Implementation Tasks

| Phase | Task | Priority | Status |
|-------|------|----------|--------|
| **1** | JSON config file loading | High | ✅ |
| **1** | Remove env var / CLI config support | High | ✅ |
| **1** | Config validation and defaults | High | ✅ |
| **2** | Embedded HTTP server setup | High | ✅ |
| **2** | Admin authentication (username/password) | High | ✅ |
| **2** | REST API endpoints | High | ✅ |
| **2** | Static HTML/JS admin dashboard | High | ✅ |
| **3** | Host role separation (dedicated mode) | High | ✅ |
| **3** | Host promotion on disconnect | High | ✅ |
| **3** | Admin host override | High | ✅ |
| **4** | Idle timeout implementation | Medium | ✅ |
| **4** | Ready-up timeout (force kick) | Medium | ✅ |
| **5** | Vote-to-kick system | Low | ✅ |
| **5** | Ban list persistence | Low | ✅ |

#### Config File Schema (`dedicated_server.json`)

```json
{
  "server": {
    "sessionName": "Community YARG Server",
    "port": 9050,
    "maxPlayers": 16,
    "password": "",
    "privacyMode": "public",
    "visibleOnLan": true,
    "registerWithLobbyServers": true
  },
  "gameplay": {
    "bandSize": 0,
    "noFailMode": false,
    "sharedSongsOnly": true,
    "allowModifiers": true,
    "enablePresetSync": true,
    "allowLateJoin": true
  },
  "timeouts": {
    "idleMinutes": 30,
    "readyUpMinutes": 5
  },
  "moderation": {
    "voteToKickEnabled": true,
    "voteToKickThreshold": 0.5,
    "banListPath": "banned_players.json"
  },
  "admin": {
    "webPort": 8080,
    "username": "admin",
    "password": "changeme",
    "allowRemoteAccess": false
  }
}
```

#### Admin Web UI Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/auth/login` | Authenticate, returns session token |
| POST | `/api/auth/logout` | Invalidate session |
| GET | `/api/status` | Server status, player count, current song |
| GET | `/api/players` | List all connected players |
| POST | `/api/players/{id}/kick` | Kick a player |
| POST | `/api/players/{id}/ban` | Ban a player |
| POST | `/api/players/{id}/promote` | Make player the new host |
| GET | `/api/settings` | Current session settings |
| PUT | `/api/settings` | Update session settings |
| POST | `/api/server/shutdown` | Graceful shutdown |
| GET | `/` | Serve admin dashboard HTML |

#### Admin Dashboard Features (Web UI)

1. **Login Page**
   - Username/password form
   - Session cookie storage

2. **Dashboard**
   - Server status (uptime, current state: lobby/playing/loading)
   - Current song info (if playing)
   - Player count: X/Y

3. **Players Tab**
   - List of all players with: name, ping, instrument, band, host badge
   - Actions: Kick, Ban, Promote to Host
   - Filter by band

4. **Settings Tab**
   - All session settings as form controls
   - Apply button (syncs to all clients)

5. **Logs Tab** (optional, future)
   - Recent events: joins, leaves, kicks, song completions

---

## ❓ Open Questions

1. ~~**Admin UI for dedicated servers** - Web-based? In-engine tool?~~ → **Resolved: Embedded web UI**
2. **Friends system** - Future "Friends Only" privacy mode?

---

## 📋 Design Notes

### Favorites/Recents for Lobbies vs Servers (Dec 23, 2025)
**Problem**: Lobbies (UPnP-based, SessionType.Lobby) are temporary - when the host closes them, the IP:port becomes invalid. It makes no sense to save a lobby to favorites or recents. Only servers (manual port forward, SessionType.Server) have stable addresses that can be bookmarked.

**Current State**:
- `LobbyBookmark` stores `address` + `port` (designed for servers)
- Discovery protocol (`DiscoveredLobbyInfo`) does NOT transmit `SessionType`
- When joining ANY discovered session, its IP is recorded to recents
- Users can favorite ANY discovered session (including ephemeral lobbies)

**Solution**:
1. Add `SessionType` field to `DiscoveredLobbyInfo` and discovery protocol
2. Add `SessionType` to `LobbyInfo` (Unity-side)
3. In `LobbyBrowserMenu.TrackPasswordSubmission()` - only record if session is a Server
4. In `DiscoveredLobbyViewType.OnFavoriteClick()` - only allow favoriting Servers
5. Optionally: show visual indicator in lobby list for Server vs Lobby type

---

## Session Notes

### Dec 16, 2025
- Implemented `SessionSettingsPanelBuilder` for settings UI
- Fixed `GameMode` enum byte casting for `Enum.IsDefined`
- `AllowedGameModes` is a BLACKLIST (modes in list are DISABLED)
- Added `LobbyUpdated` event to `DiscoveryManager`
- Fixed ping display to show actual ms values with color coding
- Increased `IsFresh` threshold to 15 seconds
- Fixed `CloneLobbyInfo` to copy `LastSeen`, `Ping`, and gameplay settings
- Added track reordering controls to PlayerView (host only)
- Integrated TrackOrderManager with LobbyRoomMenu
- Added network sync for track order (BroadcastTrackOrder, OnTrackOrderReceived)
- Fixed session-unique player IDs in `LocalPlayerIdentity` for ParrelSync testing
- Fixed nullable handling in `HandlePlayerIdentityMessage` for client identity parsing
- Integrated `TrackOrderManager` order into `MultiplayerPlayerManager.CreateMultiplayerPlayers()`
- Track order now properly applies to gameplay highway rendering (left-to-right display)

### Dec 16, 2025 (Later)
- Fixed "Multiple local profiles not added online" bug:
  - Added `BuildMultiPlayerRequestPacket` and `TryParseMultiPlayerRequestPacket` to `HandshakeBinaryPackets`
  - Created `GetAllLocalPlayerIdentities()` method to get identities for ALL local profiles
  - Updated `SendPlayerIdentityToServer()` to send all local profiles
  - Updated `HandlePlayerIdentityMessage()` to parse and create NetworkPlayerData for each identity
  - Updated host creation to add all local profiles to `_connectedPlayers`
  - Updated client connection to add all local profiles locally
  - Added `profileCount` to auth request packet for capacity checking
  - Updated `HandleAuthRequestMessage()` to validate room for ALL client profiles

### Dec 22, 2025
- Fixed "Client back button at music library" bug:
  - Refactored `ExitLibrary()` in `MusicLibraryMenu.cs` to detect multiplayer client state
  - Added `ShowLeaveLobbyDialog()` method with confirmation dialog (Cancel/Leave Lobby buttons)
  - Client now sees "Leave Lobby?" confirmation before disconnecting
  - On confirm, client properly calls `LeaveLobby()` then navigates back
  - Host behavior unchanged (syncs to clients and pops menu)
  - Added toast notification when client leaves (shown to host)
  - Fixed player disconnect detection race condition with pending player identities

- Fixed "Solo score not showing for remote players" bug:
  - Added remote solo support to `SoloBox.cs`:
    - New properties: `_isRemoteSolo`, `_remoteNoteCount`, `_remoteNotesHit`, `_remoteLastSequence`
    - New method: `UpdateRemoteSolo(soloActive, sequence, noteCount, notesHit, lastBonus)`
    - Refactored `HitPercent`, `NoteCount`, `NotesHit` to work with both local and remote solos
  - Added `UpdateRemoteSolo()` passthrough method to `TrackView.cs`
  - Updated `RemotePlayerSimulation.cs`:
    - Added `UpdateSoloDisplay()` method called in `UpdateNonCriticalState()`
    - Reads solo state from `NetworkPlayerData.SoloActive/Sequence/NoteCount/NotesHit/LastBonus`
    - Passes to `TrackView.UpdateRemoteSolo()` for display
- Fixed "Online song load time too slow" bug:
  - Root cause: No mechanism for clients to signal they finished loading to the host
  - The `SignalPlayerGameplayReady()` method existed but was never called
  - Host's `WaitForMultiplayerGameplayStartAsync()` would always timeout (12s) instead of syncing
  - **New architecture**: Client→Host→Broadcast pattern:
    1. Each client sends `GameplayLoadReady` when finished loading
    2. Host collects ready signals, marks players as ready
    3. When ALL players ready, host broadcasts `GameplayAllLoadReady` to everyone
    4. Everyone releases their barrier and starts immediately
  - Added `GameplayLoadReady` packet type (value 100) for client→host signaling
  - Added `GameplayAllLoadReady` packet type (value 101) for host→all broadcast
  - Added `BuildGameplayLoadReadyPacket()`, `TryParseGameplayLoadReadyPacket()`, `BuildGameplayAllLoadReadyPacket()` to `GameplayBinaryPackets`
  - Added `HandleGameplayLoadReadyMessage()` - host receives client signal, marks player ready
  - Added `HandleGameplayAllLoadReadyMessage()` - client receives start signal, releases barrier
  - Added `CheckAndBroadcastAllLoadReady()` - host broadcasts when all ready
  - Updated `SendGameplayLoadReady()` for proper client→host sending
  - Optimized initial network wait delays: 500ms→100ms initial, 300ms→100ms retries
  - **Result**: Gameplay starts as fast as the slowest loader, no more 12s timeout waits

- Fixed "Replay fail popup breaks score screen" bug:
  - Root cause: Dialog pushed its navigation scheme, then `UpdateNavigationScheme(true)` popped it during `InitializeMultiplayerReady()`, leaving dialog's scheme to pop our scheme when closed
  - Renamed `SetNavigationScheme()` to `InitializeNavigationEntries()` - only defines entries, no push
  - Added `dialogShown` tracking in `OnEnable()` to detect when dialog appears
  - Added `WaitForDialogAndPushScheme(dialog)` async method that waits for dialog close before pushing scheme
  - Added guard in `UpdateNavigationScheme()` to return early if `DialogManager.Instance.IsDialogShowing`
  - **Result**: Navigation scheme correctly shows "Ready Up" in multiplayer after dismissing dialog

- Fixed "Client sent to main menu when host backs from library" bug:
  - Root cause: Host broadcasts `PopMenu` when leaving Music Library, but clients in Lobby Room would pop to Main Menu
  - Updated `HandleNavigateToMenuMessage()` to check `MenuManager.Instance.CurrentMenu` before popping
  - Client now only pops if currently in `MusicLibrary`, ignores `PopMenu` if in other menus
  - **Result**: Client stays in Lobby Room when host backs out of Music Library

- Fixed "Client shows Unknown host / Host doesn't see client" bug (partial fix):
  - **Issue 1**: Client joining via LAN IP couldn't find discovered lobby because lookup only matched localhost
    - Discovery stored lobby as `127.0.0.1` but client connected via `192.168.1.122`
    - Fixed lobby lookup in `JoinLobby()` to also check if target address is local LAN IP and match against localhost
  - **Issue 2**: Host doesn't receive client's HandshakeRequest packet after song library sync (edge case)
    - Added debug logging and connection state checks
    - Rare occurrence - may be timing-related during rapid state transitions

- Fixed "Star power resurrection spurious revival" bug:
  - Root cause: Duplicate revival events when Star Power activated on host for remote player
    - `TryReviveFailedPlayers()` → calls `RevivePlayer()` → fires `OnPlayerRevived(50%)`
    - `SyncRemoteHappiness()` → sees state transition → fires `OnPlayerRevived(remoteHappiness)` again
  - Added `_wasRevivedLocally` flag to `EngineContainer` class
  - `RevivePlayer()` sets `_wasRevivedLocally = true` when Star Power revives
  - `SyncRemoteHappiness()` checks this flag and skips duplicate revival events
  - Added minimum happiness threshold (10%) to catch desync cases
  - Flag cleared when player fails again, allowing proper future revivals
  - **Result**: No more spurious revivals with 0%/2% happiness

- Fixed "Settings panel disappears after Music Library" bug:
  - Root cause: SessionSettingsPanelBuilder's layout not rebuilt when returning from MusicLibrary
  - Added `RebuildLayout()` call in `OnEnable()` when panel is already built
  - Added `RebuildLayout()` call after `ApplyVisibility()` in `Configure()`
  - Added explicit drawer expansion in `ApplyVisibility()` to ensure content is visible
  - Added debug logging to trace OnEnable/OnDisable cycle
  - **Result**: Settings panel properly displays when returning from Music Library

- Fixed "Settings panel disappears after playing a song" bug (additional fix):
  - Root cause: After gameplay, scene reloads and MenuNavigationHelper pushes menu stack rapidly
  - The build coroutine (`BuildNextFrame`) would start, then panel gets disabled before build completes
  - Coroutine would either abort or complete while disabled, leaving UI in invalid state
  - **Fix 1**: Build coroutine now checks `gameObject.activeInHierarchy` after yield and aborts if disabled
  - **Fix 2**: `OnEnable()` now validates content exists (childCount > 0, drawer refs not null)
  - **Fix 3**: If content is missing despite `_isBuilt=true`, forces a fresh rebuild
  - **Result**: Settings panel properly rebuilds after returning from gameplay

- Fixed "Settings panel still empty after playing a song" bug (further fix):
  - Root cause: `_isBuildInProgress` flag gets stuck as `true` when coroutine is interrupted
    - When `BuildNextFrame()` starts, sets `_isBuildInProgress = true`
    - Coroutine yields (`yield return null`) waiting for next frame
    - Rapid menu push sequence causes LobbyRoom to be disabled
    - **Unity stops coroutines on inactive GameObjects**
    - Code after yield never runs, so `_isBuildInProgress` stays `true`
    - On next `OnEnable()`, the stale flag prevents rebuild from starting
  - **Fix**: `OnEnable()` now detects stale `_isBuildInProgress` flag (true but `_isBuilt` is false)
  - If stale, resets `_isBuildInProgress = false` to allow fresh build to proceed
  - Added diagnostic logging to `LobbyRoomMenu.ConfigureSessionSettingsPanel()` and `LoadCurrentLobbySettings()`
  - Added explicit `SetActive(true)` call if panel's GameObject is inactive
  - **Result**: Settings panel properly rebuilds after returning from gameplay regardless of coroutine interruption

- Fixed "NoFail mode not preventing fails after toggle" bug:
  - **Symptom**: Play song with fail mode on → return to lobby → turn on NoFail → play another song → no fail meter visible BUT player can still fail out
  - **Root cause**: NoFail check only happened at GameManager level for fail sequence/UI, but engine still tracked fails internally
    - `EngineContainer.AddHappiness()` sets `HasFailed = true` when happiness drops to 0, regardless of NoFail
    - This `HasFailed` flag was sent over network via `SendGameplaySnapshot()`
    - Other clients received `hasFailed=true` and applied it to their local engine state
    - The FailMeter was hidden (because NoFail was checked for UI), but fail logic still ran
  - **Fix 1**: `GameManager.cs` - When sending network snapshots, override `hasFailed = false` if `IsNoFailActive`
    - Players can't actually fail in NoFail mode, so never report as failed over network
  - **Fix 2**: `TrackPlayer.cs` - When receiving `SyncRemoteHappiness()`, override `hasFailed = false` if `IsNoFailActive`
    - Protects against old snapshots or clients that haven't been updated yet
  - **Fix 3**: `BasePlayer.cs` - `HasPlayerFailed()` now returns `false` immediately if `IsNoFailActive`
    - Prevents visual fail state (lowered track camera) when NoFail is on
  - **Result**: NoFail mode properly prevents fails even when toggled between songs

- Fixed "Fail screen shows wrong options in multiplayer" bug:
  - **Symptom**: Host and client both saw all single-player options (Restart, Enable No Fail, Back to Library, Practice Mode)
  - **Root cause 1**: `CacheButtons()` was searching for `Button` components but prefab uses `NavigatableButton`
  - **Root cause 2**: Buttons weren't being found so all button references were null, preventing layout changes
  - **Fix 1**: Changed `GetComponentsInChildren<Button>` to `GetComponentsInChildren<NavigatableButton>`
  - **Fix 2**: Added cache reset logic if buttons weren't found on previous attempt
  - **Fix 3**: Used more reliable multiplayer detection (check `MultiplayerGameplaySync` component)
  - **Layout in multiplayer**:
    - **Host sees**: Restart + Back to Library (both sync to all clients)
    - **Client sees**: Leave Lobby only (with confirmation dialog)
  - **Result**: Proper multiplayer-specific fail menu for host and client

### Dec 26, 2025
- Fixed "Server mode shows stale lobby code" bug:
  - **Symptom**: Create Lobby → get code → leave → create Server → still shows old lobby code
  - **Root cause**: `LobbyRoomMenu` cached `_lobbyCode`, `_lanAddress`, `_wanAddress` fields weren't reset
  - **Fix**: Reset all cached address fields and visibility flags to empty/false in `OnEnable()`
  - **Result**: Server mode correctly shows only LAN/WAN addresses, no lobby code row

- Fixed "WAN address not showing on subsequent Server sessions" bug:
  - **Symptom**: Create Server → shows WAN → leave → create Server again → WAN row missing
  - **Root cause**: `PublicEndpointResolver` (STUN) caches resolved address and only fires `EndpointResolved` event when result CHANGES
  - If same public IP is resolved twice, `changed = false` and event doesn't fire
  - `LobbyRoomMenu.OnWanEndpointResolved()` never gets called on second session
  - **Fix**: Changed `CancelPublicEndpointResolution()` to call `Clear()` instead of `Cancel()`
    - `Clear()` resets `_resolvedAddress = null` and `_resolvedPort = 0`
    - Next STUN resolution will see a different value and fire the event
  - **Result**: WAN address properly displays on every Server session

- Started Band System UI implementation:
  - Created `BandNameGenerator.cs`:
    - ~100 adjectives (Electric, Flying, Mad, Cosmic, Neon, etc.)
    - ~100 nouns (Gorillas, Flamingos, Moms, Ninjas, Pickles, etc.)
    - Generates names like "Electric Gorillas", "Flying Flamingos", "Mad Moms"
    - Supports deterministic generation from seed (for network sync)
    - `Generate(lobbySeed, bandId, regenerationCount)` ensures all clients generate same name
  - Enhanced `BandManager.cs`:
    - Added `_connectionPlayerGroups` to track which players belong to each client
    - Added `_lobbySeed` for deterministic name generation
    - Added `AssignClientPlayersToBand()` - assigns entire client group to same band
    - Added `ValidateBandConfiguration()` - validates no client has more players than band size
    - Added `MovePlayerToBand()` - moves player or entire client group
    - Added `RegenerateBandName()` - generates new name for a band
    - Added `CanMovePlayerIndividually()` - checks if player can move alone
    - Added `GetMovablePlayerGroup()` - gets all players that must move together
    - Added `BandSyncData` class for comprehensive network sync
  - Enhanced `BandInfo`:
    - Added `GeneratedName` property for fun band names
    - Added `NameRegenerationCount` for deterministic regeneration
    - Added `ConnectionGroups` to track which connections have players in band
    - `DisplayName` now returns generated name if available

### Dec 27, 2025
- Fixed "Band score not syncing to host in band mode" bug:
  - **Symptom**: In multiplayer band mode, host sees remote band's score as 0 on the BandLeaderboardHUD
  - **Root cause**: Band score was becoming 0 after entering spectate mode
    - `HideLocalTracks()` makes player GameObjects inactive
    - `Update()` loop skips inactive players, so `BandScore` becomes 0
    - `SendBandScoreUpdate()` was then sending 0
  - **Fix**: Added `_savedBandScoreForNetwork` field to preserve score before hiding tracks
    - Modified `HandleBandModeFailureAsync()` to save `BandScore` before `HideLocalTracks()`
    - Modified `SendBandScoreUpdate()` to use saved score when `_hasSavedBandScore` is true
  - **Result**: Band scores now properly sync even after spectate mode

- Fixed stars not syncing when spectating another band:
  - **Symptom**: When spectating, remote band's star count didn't update on leaderboard
  - **Fix**: Added star count sync to `BandScoreUpdate` packet and `BandLeaderboardHUD`

- Fixed band-aware player reordering:
  - **Symptom**: Track reorder could move player past band boundaries
  - **Fix**: Added boundary checks to prevent moving players outside their band's player range

- Implemented "Move to Band" dropdown UI:
  - **Feature**: Host can move players between bands from lobby
  - **UI Design**: Dropdown next to existing reorder arrows (not a dialog)
  - **PlayerView.cs changes**:
    - Added `moveToBandDropdown` (TMP_Dropdown) serialized field
    - Added `OnMoveToBandSelected` event for when band selection changes
    - Added `SetupMoveToBandDropdown()` - initializes listener
    - Added `ConfigureMoveToBandDropdown()` - populates with band options, shows player count with ⚠ for over-capacity
    - Added `HideMoveToBandDropdown()` - hides when bands disabled
    - Dropdown shows all bands (can move even to over-capacity bands)
    - Removed "(Full)" indicator - all bands always selectable for flexibility
    - Added debug logging to track dropdown visibility issues
  - **LobbyRoomMenu.cs changes**:
    - Extended `CreatePlayerViewWithBandIndex()` to pass band info for dropdown configuration
    - Added `OnPlayerMoveToBand()` handler - calls `BandManager.MovePlayerToBand()`, broadcasts sync, refreshes UI
    - Wires up `playerView.OnMoveToBandSelected` event
    - Added `UpdateBandValidationState()` - disables Browse Songs button when bands are over-capacity
    - `OnBrowseSongsClicked()` validates bands and shows **toast notification** if invalid
  - **BandManager.cs changes**:
    - Modified `MovePlayerToBand()` to allow moves even when target band is at/over capacity
    - Added `ValidateForGameStart()` - checks all bands are within capacity limits
    - Added `GetOverCapacityBands()` - returns list of bands with too many players
    - Added `IsBandOverCapacity()` - checks if specific band exceeds band size
  - **SessionSettingsPanelBuilder.cs changes**:
    - Fixed slider to use `OnSliderChange()` instead of `SetValueWithoutNotify()` so `ValueChanged` fires on drag
  - **Behavior**: Host can freely reorganize players between bands, but cannot start game until all bands are valid
  - **Validation feedback**: Toast notification shown when host tries to Browse Songs with invalid bands
  - **Group moves**: Backend `BandManager.GetMovablePlayerGroup()` handles keeping same-client players together
  - **Next step**: Wire up TMP_Dropdown to PlayerView prefab in Unity Editor

### Dec 30, 2025
- Marked Band System UI as complete (all tasks finished)
- Fixed "Setlist instrument filtering too restrictive (Online only)" bug:
  - **Symptom**: In online multiplayer setlists, instruments/difficulties were filtered based on ALL songs, making some instruments unavailable even though they exist in the current song
  - **Root cause**: `DifficultySelectMenu` filtered instruments/difficulties by checking ALL songs in `_songList`, but online multiplayer shows difficulty select per-song
  - **Fix**: Modified `SetActivePlayer()` and `UpdatePossibleDifficulties()` to detect online multiplayer mode
    - **Online multiplayer setlist**: Only check current song (`GlobalVariables.State.CurrentSong`) since difficulty select shows per-song
    - **Local/offline mode**: Keep original behavior (check ALL songs in setlist)
  - **Harmony fix**: Changed `_maxHarmonyIndex` calculation
    - Online multiplayer: Uses current song's `VocalsCount` only
    - Local mode: Uses `Mathf.Min()` across all songs (original restrictive behavior)
  - **Result**: In online multiplayer setlists, each song shows its own available instruments/difficulties/harmonies