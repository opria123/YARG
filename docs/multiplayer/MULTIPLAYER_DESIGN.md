# YARG Multiplayer System Design

> **Status**: Implementation In Progress  
> **Last Updated**: December 13, 2025  
> **Authors**: YARG Team

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Session Types](#session-types)
4. [Settings Schema](#settings-schema)
5. [Lobby Server](#lobby-server)
6. [Band System](#band-system)
7. [Track Ordering](#track-ordering)
8. [UPnP Integration](#upnp-integration)
9. [Remaining Work](#remaining-work)

---

## Overview

YARG multiplayer allows players to play together in online sessions. The system is designed with **resilience** in mind - if YARG infrastructure goes offline, players can still host and join servers directly.

### Core Principles

1. **Always Works Offline**: Servers work without any external services
2. **Convenience Layer**: Lobbies add UPnP + codes for ease of use
3. **Community Friendly**: Users can run their own lobby servers
4. **Scalable**: Support for large sessions via Band system

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                              PLAYERS                                     │
│                                                                          │
│   ┌─────────────┐    ┌─────────────┐    ┌─────────────┐                 │
│   │   Host      │    │   Client    │    │   Client    │                 │
│   │  (Server)   │    │             │    │             │                 │
│   └──────┬──────┘    └──────┬──────┘    └──────┬──────┘                 │
│          │                  │                  │                         │
└──────────┼──────────────────┼──────────────────┼─────────────────────────┘
           │                  │                  │
           │     ┌────────────┴────────────┐     │
           │     │      LiteNetLib         │     │
           │     │   (P2P Connections)     │     │
           │     └────────────┬────────────┘     │
           │                  │                  │
           ▼                  ▼                  ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                         DISCOVERY METHODS                                │
│                                                                          │
│   ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐        │
│   │  LAN Discovery  │  │  Lobby Server   │  │ Direct Connect  │        │
│   │   (UDP Broadcast)│  │   (HTTP API)    │  │   (IP:Port)     │        │
│   └─────────────────┘  └─────────────────┘  └─────────────────┘        │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## Session Types

### Server (Manual Port Forward)

- **Requires**: Manual port forwarding on router
- **Discovery**: LAN broadcast, Lobby Server registration (opt-in), Direct connect
- **No lobby code**: Players connect via IP:Port or find in browser
- **Works offline**: No external services required
- **Use case**: Technical users, persistent community servers

### Lobby (UPnP + Lobby code)

- **Requires**: UPnP-capable router + Lobby Server
- **Discovery**: Lobby code, LAN broadcast, Lobby Server browser
- **Gets lobby code**: 6-character hex code (e.g., `A3F2B1`)
- **If UPnP fails**: Hard fail with message suggesting Server mode
- **Use case**: Casual users wanting easy friend invites

### Dedicated Server (Headless)

- **Same as Server** but runs headless
- **Can register with lobby servers** for public discovery
- **No lobby code**: Not a lobby
- **Extra settings**: Idle timeout, ready-up timeout, vote-to-kick
- **Use case**: Community-run persistent servers

---

## Settings Schema

### File Structure

```
persistentDataPath/
├── network_settings.json      # Global: Lobby Servers + defaults
├── lobby_bookmarks.json       # Existing: Bookmarks + SessionPresets
└── dedicated_server.json      # Dedicated server only
```

### network_settings.json

```json
{
  "lobbyServers": [
    {
      "id": "yarg-official",
      "displayName": "YARG Official",
      "url": "https://lobby.yarg.in",
      "enabled": true,
      "isBuiltIn": true,
      "createdAt": 1702500000,
      "lastSuccessAt": 1702500000,
      "consecutiveFailures": 0
    }
  ],
  "defaultPort": 9050,
  "localPlayersFirst": true,
  "lastUsedLobbySettings": {
    "sessionName": "My Session",
    "maxPlayers": 8,
    "bandSize": 4,
    "privacyMode": 0,
    "noFailMode": false,
    "sharedSongsOnly": true
  }
}
```

### SessionPreset (in lobby_bookmarks.json)

```json
{
  "sessionPresets": [
    {
      "id": "abc123",
      "presetName": "Friday Night Jam",
      "sessionType": 1,
      "createdAt": 1702500000,
      "lastHostedAt": 1702500000,
      
      "sessionName": "Friday Night Jam",
      "port": 9050,
      "password": "",
      "privacyMode": 0,
      
      "maxPlayers": 16,
      "bandSize": 4,
      
      "visibleOnLan": true,
      "registerWithLobbyServers": true,
      
      "noFailMode": false,
      "sharedSongsOnly": true,
      "enablePresetSync": true,
      "allowLateJoin": true,
      "allowedInstruments": [],
      
      "localPlayersFirst": null
    }
  ]
}
```

### Enums

```csharp
public enum SessionType
{
    Server = 0,  // Manual port forward, no code
    Lobby = 1    // UPnP + Lobby code required
}

public enum SessionPrivacyMode
{
    Public = 0,    // Visible in LAN + Lobby Server browsers
    Private = 1,   // Password required, still visible
    Unlisted = 2   // Code/Direct only, hidden from browsers
}
```

### Setting Definitions

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `sessionName` | string | "YARG Session" | Display name shown in browsers (not IP) |
| `port` | int | 9050 | Port to listen on |
| `password` | string | "" | Password to join (empty = none) |
| `privacyMode` | enum | Public | Visibility in browsers |
| `maxPlayers` | int | 8 | Maximum players (2-64) |
| `bandSize` | int | 0 | Players per band (0 = disabled, 2-8) |
| `visibleOnLan` | bool | true | Show in LAN discovery |
| `registerWithLobbyServers` | bool | true | Register with enabled lobby servers |
| `noFailMode` | bool | false | Disable failing for all players |
| `sharedSongsOnly` | bool | true | Only show songs everyone can play |
| `enablePresetSync` | bool | true | Sync camera/color presets |
| `allowLateJoin` | bool | true | Allow joining during gameplay |
| `allowedInstruments` | int[] | [] | Allowed instruments (empty = all) |
| `localPlayersFirst` | bool? | null | Override global track ordering |

### Dedicated Server Settings

```json
{
  "presetId": "abc123",
  "idleTimeoutMinutes": 30,
  "readyUpTimeoutMinutes": 5,
  "voteToKickEnabled": true,
  "voteToKickThreshold": 0.5,
  "adminPassword": "secret"
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `presetId` | string? | null | Reference to saved preset (or inline settings) |
| `idleTimeoutMinutes` | int | 0 | Kick host after inactivity (0 = disabled) |
| `readyUpTimeoutMinutes` | int | 0 | Kick players not readying up (0 = disabled) |
| `voteToKickEnabled` | bool | false | Allow players to vote kick |
| `voteToKickThreshold` | float | 0.5 | Votes needed (0.5 = majority) |
| `adminPassword` | string | "" | Password for admin UI |

---

## Lobby Server

### Purpose

The Lobby Server provides:
1. **Lobby listing** - Browse public servers/lobbies
2. **Lobby codes** - Short codes for easy lobby sharing (Lobby mode only)
3. **NAT traversal info** - Public IP discovery

### API Endpoints

```
GET  /health                    # Health check
GET  /api/lobbies               # List all active sessions
POST /api/lobbies               # Register/heartbeat a session
DELETE /api/lobbies/{id}        # Remove a session

# Lobby code endpoints (for Lobby mode)
POST /api/lobbies/code          # Generate lobby code
GET  /api/lobbies/code/{code}   # Lookup by code
DELETE /api/lobbies/code/{code} # Release code
```

### Lobby Code Format

- **Format**: 6-character uppercase hexadecimal (e.g., `A3F2B1`)
- **Lifecycle**: Created when lobby starts, released when lobby ends
- **Reuse**: Codes can be reused after release

### Multiple Lobby Servers

Users can configure Multiple Lobby Servers:
- YARG Official (built-in, can disable but not delete)
- Community lobby servers (user-added)

When hosting:
- Session is registered with ALL enabled lobby servers
- Lobby code comes from first successful registration

When browsing:
- Results aggregated from all enabled lobby servers
- Deduplicated by session ID

### Self-Hosting

Community members can run their own lobby server:
1. Deploy Lobby Server
2. Users add URL in Settings → Network → Lobby Servers
3. Sessions registered there are visible to users with that lobby server

---

## Band System

### Overview

Bands allow large sessions (32+ players) to play together without overwhelming the UI. Players are split into bands that compete against each other.

### How It Works

```
32 Players → 8 Bands of 4
           ↓
Each player sees only their band's 4 tracks
           ↓
Unison bonuses calculated per-band
           ↓
End of song: Compare band scores
```

### Band Assignment

```
Algorithm:
1. Get all ready players
2. If bandSize == 0 OR playerCount <= bandSize:
   → Single group, no bands
3. Else:
   → numBands = ceil(playerCount / bandSize)
   → Distribute players evenly
   
Examples:
- 7 players, bandSize=4 → Band1: 4, Band2: 3
- 10 players, bandSize=4 → Band1: 4, Band2: 3, Band3: 3
- 16 players, bandSize=4 → Band1: 4, Band2: 4, Band3: 4, Band4: 4
```

### Band Scoring

- Each band gets a combined score and star rating
- Bands with fewer players use percentage-based scoring for fairness
- Tie-breaker: Higher percentage wins

### Band Management

- Host can move players between bands in lobby
- Players can request to move (host approves)
- Bands are reshuffled when returning to lobby after songs

### Networking

Within band (detailed):
- Note hit/miss events
- Combo updates  
- Star power activation
- Unison phrase coordination

Cross-band (lightweight):
- Aggregate score per band
- Band star rating
- Achievement events ("Band X got FC!")

---

## Track Ordering

### Priority

1. **Local players first** (if enabled)
2. **Host-defined order** (can reorder in lobby)
3. **Join order** (default)

### With Bands

1. Bands ordered by host
2. Within each band:
   - Local players first (if enabled)
   - Then by host-defined order

### UI Controls

```
Lobby Room:
┌─────────────────────────────────────────┐
│ Band 1                                  │
│ ├── Player1 (You) 🎸     [↑][↓]        │
│ ├── Player2       🥁     [↑][↓][Move]  │
│ └── Player3       🎤     [↑][↓][Move]  │
├─────────────────────────────────────────┤
│ Band 2                                  │
│ ├── Player4       🎸     [↑][↓][Move]  │
│ └── Player5       🥁     [↑][↓][Move]  │
└─────────────────────────────────────────┘

[↑][↓] = Reorder within band (host only)
[Move] = Move to different band (host only)
```

---

## UPnP Integration

### Purpose

Automatically open ports on user's router for Lobby mode.

### Flow

```
Create Lobby
     │
     ▼
┌─────────────┐
│ Discover    │ ← SSDP multicast
│ UPnP Device │
└──────┬──────┘
       │
       ▼
┌─────────────┐
│ Request     │ ← SOAP AddPortMapping
│ Port Map    │
└──────┬──────┘
       │
   ┌───┴───┐
   │       │
Success  Failure
   │       │
   ▼       ▼
Continue  Hard fail
with      "Cannot create lobby.
lobby     Try hosting a Server instead."
```

### Implementation Location

- `YARG.Net/Utilities/UPnP/` - UPnP client, discovery, and SOAP handlers
- `Assets/Script/Networking/UPnP/UPnPPortForwarder.cs` - Unity wrapper

---

## Remaining Work

### Phase 1: Settings Infrastructure ✅ COMPLETE

- [x] Create `LobbyServerEndpoint` class
- [x] Create `SessionPreset` class  
- [x] Create `NetworkGlobalSettings` class
- [x] Create `NetworkSettingsStore` (persistence)
- [x] Create `DedicatedServerConfig` class
- [x] Add enums (`SessionType`, `SessionPrivacyMode`)
- [x] Migrate existing `HostedLobbyPreset` data (in LobbyBookmarkStore)
- [ ] Add Lobby Servers UI to Settings menu

### Phase 2: Lobby Server Enhancements ✅ COMPLETE

- [x] Choose language: **C# (ASP.NET Core)** - matches YARG ecosystem
- [x] Add lobby code generation endpoint (`POST /api/lobbies/code`)
- [x] Add lobby code lookup endpoint (`GET /api/lobbies/code/{code}`)
- [x] Add lobby code release endpoint (`DELETE /api/lobbies/code/{code}`)
- [x] Client-side: `LobbyCodeClient` for code operations
- [ ] UI: "Join by Code" flow

### Phase 3: UPnP Implementation ✅ COMPLETE

- [x] Implement `UPnPClient` in YARG.Net
- [x] SSDP device discovery (`UPnPDiscovery`)
- [x] SOAP port mapping requests (`UPnPSoap`)
- [x] Unity wrapper (`UPnPPortForwarder`)
- [ ] Integrate into lobby creation flow
- [ ] Error handling and user feedback UI

### Phase 4: Band System ✅ COMPLETE

- [x] `BandManager` - band assignment and tracking
- [x] `BandInfo` - per-band data structure
- [x] Network messages (`BandAssignmentMessage`, `BandScoreUpdateMessage`, `BandLeaderboardMessage`)
- [ ] Lobby UI for band management
- [ ] Per-band unison calculation
- [ ] End-of-song band comparison UI

### Phase 5: Track Ordering ✅ COMPLETE

- [x] `TrackOrderManager` - handles ordering logic
- [x] Support for `localPlayersFirst` setting
- [x] Support for host custom ordering
- [x] Support for band grouping
- [x] Network messages (`TrackOrderMessage`, `TrackReorderRequestMessage`)
- [ ] Add reorder controls to lobby UI
- [ ] Apply order in gameplay renderer

### Phase 6: Session Type Differentiation ✅ COMPLETE

- [x] `SessionManager` - manages session lifecycle
- [x] Server creation flow (no UPnP, no code)
- [x] Lobby creation flow (UPnP + code)
- [x] `SessionState` enum for state tracking
- [x] `SessionStartResult` for detailed results
- [ ] UI to choose between Server/Lobby
- [ ] Graceful messaging for failures in UI

### Phase 7: Gameplay Settings ✅ COMPLETE

- [x] `MultiplayerGameplaySettings` manager
- [x] No-fail mode enforcement
- [x] Instrument restrictions
- [x] Modifier blocking
- [x] Score submission blocking
- [x] Allowed Instruments setting (UI with clickable icons)
- [x] Local Players First setting (toggle)
- [ ] Shared songs filter integration
- [ ] Preset sync (camera/colors) implementation
- [ ] Late join handling

### Phase 8: Dedicated Server ⬜ NOT STARTED

- [x] `DedicatedServerConfig` class (Phase 1)
- [ ] Config file loading
- [ ] Environment variable support
- [ ] Idle timeout implementation
- [ ] Ready-up timeout implementation
- [ ] Vote-to-kick system
- [ ] Admin UI (future)

### UI Integration ⬜ NOT STARTED

- [ ] Settings menu for Lobby Servers management
- [ ] Lobby creation wizard (Server vs Lobby choice)
- [ ] Join by Code dialog
- [ ] Band display in lobby
- [ ] Track reordering controls
- [ ] Session restrictions display

### Bug Fixes Already Completed ✅

- [x] Client leaving lobby when host navigates back
- [x] Host UI not updating when client disconnects
- [x] Track layout shift on disconnect (now grays out)
- [x] Client navigation on disconnect (goes to LobbyBrowser)
- [x] Menu stack on quit mid-song

---

## Implementation Summary

### New Files Created

**YARG.Net (Core Networking):**
- `LobbyServer/LobbyCodeClient.cs` - Client for lobby code operations
- `Utilities/UPnP/UPnPClient.cs` - Main UPnP client
- `Utilities/UPnP/UPnPDiscovery.cs` - SSDP device discovery
- `Utilities/UPnP/UPnPSoap.cs` - SOAP requests for port mapping

**YARG.Lobby Server:**
- Updated `Program.cs` with lobby code endpoints

**Unity (Assets/Script/Networking):**
- `Settings/SessionEnums.cs` - SessionType, SessionPrivacyMode
- `Settings/LobbyServerEndpoint.cs` - Lobby server configuration
- `Settings/SessionPreset.cs` - Session configuration
- `Settings/NetworkGlobalSettings.cs` - Global settings
- `Settings/NetworkSettingsStore.cs` - Persistence
- `Settings/DedicatedServerConfig.cs` - Headless server config
- `UPnP/UPnPPortForwarder.cs` - Unity UPnP wrapper
- `Bands/BandManager.cs` - Band assignment and tracking
- `Bands/BandNetworkMessages.cs` - Band sync messages
- `Tracks/TrackOrderManager.cs` - Track ordering logic
- `Tracks/TrackOrderMessages.cs` - Order sync messages
- `Session/SessionManager.cs` - Session lifecycle
- `Gameplay/MultiplayerGameplaySettings.cs` - Gameplay restrictions

---

## Open Questions

1. **Admin UI for dedicated servers**: Web-based? In-engine tool?
2. **Friends system**: Future consideration for "Friends Only" privacy mode?

## Decisions Made

1. **Server language**: C# (ASP.NET Core) - consistent with YARG ecosystem (YARG, YARG.Core, YARG.Net)

---

## References

- [LiteNetLib](https://github.com/RevenantX/LiteNetLib) - Networking library
- [YARG.Networking](../YARG.Networking/) - Networking package
- [STUN RFC 5389](https://tools.ietf.org/html/rfc5389) - NAT traversal
- [UPnP IGD](http://upnp.org/specs/gw/UPnP-gw-WANIPConnection-v2-Service.pdf) - Port mapping
