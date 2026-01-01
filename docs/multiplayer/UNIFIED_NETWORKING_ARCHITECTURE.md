# Unified Networking Architecture

## Overview

This document describes the refactored networking architecture where **all host actions go through a unified message-based pattern**. This ensures identical behavior across:
- In-game hosting (player creates lobby)
- Lobbies (same as in-game hosting)
- Dedicated servers (server process separate from players)

## Core Principle

**Every host action is a request to the server, even if "the server" is yourself.**

This means:
1. Menu code calls a single method (e.g., `AdvanceAfterScoreScreen()`)
2. The adapter ALWAYS sends a request packet
3. The server (which may be the same process) validates and executes
4. The server broadcasts the result to all clients

## Key Changes

### Before (Dual-Path Pattern)
```csharp
// Menu code had to know about hosting vs dedicated server
if (adapter.IsHosting)
{
    adapter.BroadcastNavigateToMusicLibrary();  // Direct execution
}
else if (adapter.IsLocalPlayerHost)
{
    adapter.RequestNavigateToMusicLibrary();    // Request pattern
}
```

### After (Unified Pattern)
```csharp
// Menu code uses single method - routing is internal
adapter.NavigateToMusicLibrary();

// Internally, adapter ALWAYS routes to server:
// - If IsHosting: Execute locally via ProcessNavigationAction()
// - If not hosting: Send NavigationRequest packet
//
// Server handler (whether self or remote):
// - Validates sender has host authority
// - Executes BroadcastNavigateToMusicLibrary()
```

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                        MENU CODE                                 │
│  (Uses single API regardless of hosting mode)                   │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                    INetworkingService                           │
│  NavigateToMusicLibrary()                                       │
│  StartShow()                                                    │
│  AdvanceAfterScoreScreen()                                      │
│  RestartGameplay()                                              │
│  QuitToLibrary()                                                │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                 LiteNetNetworkingAdapter                        │
│                                                                 │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │              Action Router (Internal)                     │  │
│  │                                                           │  │
│  │  if (_isHosting)                                          │  │
│  │      ExecuteActionLocally(action, data);                  │  │
│  │  else                                                     │  │
│  │      SendActionRequest(action, data);                     │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                 │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │              Server Logic (Validates & Executes)          │  │
│  │                                                           │  │
│  │  HandleActionRequest(connection, action, data):           │  │
│  │      if (HasHostAuthority(connection))                    │  │
│  │          ExecuteActionLocally(action, data);              │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

## Host Authority Concept

Instead of `IsHosting` vs `IsLocalPlayerHost`, we use a single concept:

### `HasHostAuthority`
Returns `true` if the local client can perform host actions:
- In-game host: Always `true` (you ARE the server)
- Dedicated server client: `true` if you're the designated host
- Regular client: Always `false`

### Server-Side Validation
```csharp
private bool HasHostAuthority(INetConnection connection)
{
    // If this is a dedicated server, check if sender is designated host
    if (_isDedicatedServer)
    {
        var player = GetPlayerByConnection(connection);
        return player?.IsHost == true;
    }
    
    // For in-game hosting, the "request" comes from self (null connection = local)
    return connection == null;
}
```

## Host Actions to Refactor

### Navigation Actions
| Old Method | New Unified Method |
|------------|-------------------|
| `BroadcastNavigateToMusicLibrary()` | `NavigateToMusicLibrary()` |
| `RequestNavigateToMusicLibrary()` | (internal routing) |
| `BroadcastNavigateToLobbyRoom()` | `NavigateToLobbyRoom()` |
| `RequestNavigateToLobbyRoom()` | (internal routing) |
| `BroadcastPopMenu()` | `PopAllPlayersMenu()` |

### Setlist/Show Actions
| Old Method | New Unified Method |
|------------|-------------------|
| `BroadcastStartShow()` | `StartShow()` |
| `RequestStartShow()` | (removed - use `StartShow()`) |
| `RequestAddToSetlist()` | `RequestAddToSetlist()` (unchanged - any player can request) |
| `RequestRemoveFromSetlist()` | `RequestRemoveFromSetlist()` (unchanged - any player can request) |

### Gameplay Actions
| Old Method | New Unified Method |
|------------|-------------------|
| `BroadcastRestartGameplay()` | `RestartGameplay()` |
| `BroadcastQuitToLibrary()` | `QuitToLibrary()` |
| `AdvanceAfterScoreScreen()` | `AdvanceAfterScoreScreen()` (unified) |

## Implementation Status

All phases have been completed. The unified networking architecture is now fully implemented.

### Phase 1: Create Infrastructure ✅
1. ✅ Added `HasHostAuthority` property
2. ✅ Created `ExecuteHostAction()` internal router method
3. ✅ Created `RouteHostAction()` unified request packet structure
4. ✅ Created `HostActionType` enum for all host actions

### Phase 2: Refactor Navigation ✅
1. ✅ Merged `BroadcastNavigateToMenu` and `RequestNavigateToMenu` into unified methods
2. ✅ Updated handlers to use `ExecuteHostAction()`
3. ✅ Updated menu code to use `NavigateToMusicLibrary()`, `NavigateToLobbyRoom()`, `PopAllPlayersMenu()`

### Phase 3: Refactor Setlist/Show ✅
1. ✅ Unified `StartShow()` method (deprecated `StartShowDirect()` and `RequestStartShow()`)
2. ✅ `RequestAddToSetlist()` and `RequestRemoveFromSetlist()` work for both host and client
3. ✅ Updated music library menu to use unified methods

### Phase 4: Refactor Gameplay/Score ✅
1. ✅ Unified `AdvanceAfterScoreScreen()` - now in unified host actions region
2. ✅ Unified `RestartGameplay()` and `QuitToLibrary()` actions
3. ✅ Updated score screen menu to use unified method

### Phase 5: Clean Up ✅
1. ✅ All menu code uses `HasHostAuthority` instead of dual `IsHosting`/`IsLocalPlayerHost` checks
2. ✅ Removed deprecated `StartShowDirect()` and `RequestStartShow()` methods
3. ✅ Updated documentation (this document)

## Benefits

1. **Single code path in menus** - No more `if (IsHosting) ... else if (IsLocalPlayerHost)`
2. **Consistent behavior** - Same execution flow regardless of hosting mode
3. **Easier testing** - One path to test
4. **Future-proof** - Adding new hosting modes is trivial
5. **Standard pattern** - Matches how Unreal, Mirror, and other frameworks work

## Backwards Compatibility

The public `IsHosting` and `IsLocalPlayerHost` properties will remain for any code that genuinely needs to know about server/client roles (e.g., packet routing, connection handling). But menu code should use `HasHostAuthority` for permission checks.
