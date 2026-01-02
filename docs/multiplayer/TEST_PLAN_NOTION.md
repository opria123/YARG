# Multiplayer Networking Test Plan

**Version:** 1.1  
**Date:** January 2026  
**Scope:** All changes from multiplayer networking code review and short-term improvements

---

# 📋 Pre-Test Checklist

- [ ] Clear all PlayerPrefs/settings from previous sessions
- [ ] Ensure no other YARG instances are running
- [ ] Verify firewall allows UDP on port 7777 (or configured port)
- [ ] Have Task Manager ready (in case of hangs)
- [ ] Enable verbose logging for detailed diagnostics

---

# 🔍 2. Lobby Discovery Tests

## 2.1 LAN Discovery - Basic Flow

> **Related Changes:** `LiteNetDiscovery.cs` deadlock fix, removed redundant `_lobbiesLock`

- [ ] **DISC-001** [P0] Host creates discoverable lobby
    - Steps: Host creates public lobby → Client opens lobby browser
    - Expected: Client sees lobby in list within 5 seconds

- [ ] **DISC-002** [P0] Lobby info accuracy
    - Steps: Host creates lobby with specific settings → Client views lobby in browser
    - Expected: All settings (name, players, password status) match

- [ ] **DISC-003** [P0] **No hang on discovery**
    - Steps: Client opens lobby browser → Discovery ping sent → Host responds
    - Expected: Client UI remains responsive, no freeze

- [ ] **DISC-004** [P1] Multiple lobbies discovered
    - Steps: Start 3 hosts with different lobbies → Client opens browser
    - Expected: All 3 lobbies appear in list

- [ ] **DISC-005** [P1] Lobby disappears when closed
    - Steps: Host creates lobby → Client sees it → Host closes lobby
    - Expected: Lobby removed from list within 15 seconds

- [ ] **DISC-006** [P1] Rapid discovery refresh
    - Steps: Client spams refresh button 10 times quickly
    - Expected: No crashes, UI remains responsive

## 2.2 Direct Connect Discovery

- [ ] **DISC-010** [P0] Direct connect by IP
    - Steps: Host creates lobby → Client enters host IP:port directly
    - Expected: Client connects successfully

- [ ] **DISC-011** [P1] Direct connect invalid IP
    - Steps: Client enters non-existent IP → Attempts connect
    - Expected: Graceful timeout with error message

- [ ] **DISC-012** [P1] Direct connect wrong port
    - Steps: Host on port 7777 → Client connects to port 7778
    - Expected: Graceful failure, no hang

## 2.3 Discovery Thread Safety (Deadlock Prevention)

> **Related Changes:** Removed nested locking in `ProcessDiscoveryResponse`, removed `_lobbiesLock`

- [ ] **DISC-020** [P0] Concurrent discovery operations
    - Steps: Start discovery → While discovering, clear lobbies → Refresh again
    - Expected: No deadlock, operations complete

- [ ] **DISC-021** [P1] Discovery during lobby join
    - Steps: Start discovery → Click join on a lobby → Discovery continues in background
    - Expected: Both operations succeed without blocking

- [ ] **DISC-022** [P1] Rapid start/stop discovery
    - Steps: Start discovery → Stop immediately → Start again → Repeat 20 times
    - Expected: No resource leaks, no hangs

---

# 🏠 3. Lobby Lifecycle Tests

## 3.1 Lobby Creation

> **Related Changes:** `NetworkLobbyManager.cs`, `CreateLobbyInfo` fixes (SessionType, Password)

- [ ] **LOB-001** [P0] Create public lobby
    - Steps: Click Create Lobby → Set to Public → Confirm
    - Expected: Lobby created, host in lobby

- [ ] **LOB-002** [P0] Create private (password) lobby
    - Steps: Create lobby with password "test123"
    - Expected: Lobby shows HasPassword=true

- [ ] **LOB-003** [P1] Create unlisted lobby
    - Steps: Create lobby with Unlisted mode
    - Expected: Lobby not visible in discovery

- [ ] **LOB-004** [P1] Lobby code generation (Lobby mode)
    - Steps: Create lobby in "Lobby" session type
    - Expected: 6-character lobby code generated

- [ ] **LOB-005** [P1] Server mode (no lobby code)
    - Steps: Create lobby in "Server" session type
    - Expected: No lobby code, IsServer=true

## 3.2 Lobby Joining

> **Related Changes:** Fixed duplicate `OnLobbyJoined` bug

- [ ] **LOB-010** [P0] Join public lobby
    - Steps: Host creates public lobby → Client joins
    - Expected: Client enters lobby, sees host

- [ ] **LOB-011** [P0] Join password-protected lobby
    - Steps: Host creates lobby with password → Client enters correct password
    - Expected: Client joins successfully

- [ ] **LOB-012** [P0] Join with wrong password
    - Steps: Host creates lobby with password → Client enters wrong password
    - Expected: Join rejected with clear error

- [ ] **LOB-013** [P0] **No duplicate OnLobbyJoined**
    - Steps: Client joins lobby → Check logs/event handlers
    - Expected: OnLobbyJoined fires exactly once

- [ ] **LOB-014** [P1] Join full lobby
    - Steps: Host creates 2-player lobby → Client 1 joins → Client 2 tries to join
    - Expected: Client 2 rejected with "lobby full"

- [ ] **LOB-015** [P1] Rejoin after disconnect
    - Steps: Client joins → Client disconnects → Client rejoins
    - Expected: Successful rejoin, clean state

## 3.3 Lobby Leaving

- [ ] **LOB-020** [P0] Client leaves lobby
    - Steps: Client in lobby → Client clicks Leave
    - Expected: Client returns to menu, host sees departure

- [ ] **LOB-021** [P0] Host leaves (closes lobby)
    - Steps: Host with clients → Host leaves
    - Expected: All clients disconnected with message

- [ ] **LOB-022** [P1] Client force-quit
    - Steps: Client in lobby → Client Alt+F4s
    - Expected: Host sees client leave within timeout

- [ ] **LOB-023** [P1] Host force-quit
    - Steps: Host with clients → Host Alt+F4s
    - Expected: Clients detect disconnect, return to menu

---

# 👥 4. Player Management Tests

## 4.1 Player Tracking

> **Related Changes:** `NetworkPlayerManager.cs`, `NetworkPlayerExtensions.cs`

- [ ] **PLR-001** [P0] Host player added on create
    - Steps: Host creates lobby
    - Expected: Host appears in player list as host

- [ ] **PLR-002** [P0] Client player added on join
    - Steps: Client joins lobby
    - Expected: Client appears in player list

- [ ] **PLR-003** [P1] Multiple local players
    - Steps: Host adds 2 local profiles → Both join lobby
    - Expected: Both players tracked correctly

- [ ] **PLR-004** [P0] Player name display
    - Steps: Set profile name "TestPlayer" → Join lobby
    - Expected: Name shows correctly to all

- [ ] **PLR-005** [P0] Player instrument display
    - Steps: Select guitar → Join lobby
    - Expected: Instrument visible to all players

## 4.2 Player Extensions (LINQ Helpers)

> **Related Changes:** `NetworkPlayerExtensions.cs` - `SittingOut` property fix

- [ ] **PLR-010** [P1] GetAllPlayersSafe
    - Steps: 3 players in lobby → Query all players
    - Expected: Returns all 3 players

- [ ] **PLR-011** [P1] GetLocalPlayers
    - Steps: Host with 2 local, 1 remote → Query local
    - Expected: Returns 2 local players

- [ ] **PLR-012** [P1] GetRemotePlayers
    - Steps: Host with 2 local, 1 remote → Query remote
    - Expected: Returns 1 remote player

- [ ] **PLR-013** [P1] GetSittingOutPlayers
    - Steps: 1 player sitting out → Query sitting out
    - Expected: Returns correct player

- [ ] **PLR-014** [P1] GetActivePlayers
    - Steps: 2 ready, 1 sitting out → Query active
    - Expected: Returns 2 active players

- [ ] **PLR-015** [P1] Null safety
    - Steps: Query with null dictionary
    - Expected: Returns empty, no exception

## 4.3 Player State Changes

- [ ] **PLR-020** [P0] Player changes instrument
    - Steps: Client changes from guitar to drums
    - Expected: All players see update

- [ ] **PLR-021** [P0] Player changes difficulty
    - Steps: Client changes from Medium to Expert
    - Expected: All players see update

- [ ] **PLR-022** [P2] Player changes name mid-lobby
    - Steps: Client renames profile
    - Expected: All players see new name

---

# 🎸 5. Band System Tests

## 5.1 Band Initialization & Configuration

> **Related Changes:** `BandManager.cs`, `NetworkGuards.TryGetBandManager`

- [ ] **BAND-001** [P0] Band system disabled (size=0)
    - Steps: Create lobby with band size 0 → Add 8 players
    - Expected: All players in single band (band 0)

- [ ] **BAND-002** [P0] Band system enabled (size=4)
    - Steps: Create lobby with band size 4 → Add 8 players
    - Expected: Players split into 2 bands of 4

- [ ] **BAND-003** [P0] Band size validation on join
    - Steps: Band size = 2 → Client with 3 local profiles tries to join
    - Expected: Error shown: increase band size or remove profiles

- [ ] **BAND-004** [P1] Band initialization persistence
    - Steps: Initialize band system → Change band size → Check lobby seed
    - Expected: Seed preserved, players reassigned

- [ ] **BAND-005** [P1] IsBandSystemActive check
    - Steps: Band size = 4, 4 players in 1 band → Check IsBandSystemActive
    - Expected: Returns false (only 1 band exists)

- [ ] **BAND-006** [P1] AreBandsEnabled check
    - Steps: Band size > 0 → Check AreBandsEnabled
    - Expected: Returns true even with single band

## 5.2 Band Assignment (Auto-Assignment)

- [ ] **BAND-010** [P0] First player gets band 0
    - Steps: Band size = 4 → Host joins
    - Expected: Host in band 0

- [ ] **BAND-011** [P0] Connection group stays together
    - Steps: Client has 2 local profiles → Client joins
    - Expected: Both profiles in same band

- [ ] **BAND-012** [P0] Auto-create new band when full
    - Steps: Band 0 has 4 players (full) → New client joins
    - Expected: New client in band 1

- [ ] **BAND-013** [P1] Fill existing band before creating new
    - Steps: Band size = 4, Band 0 has 2 players → Client with 2 profiles joins
    - Expected: Client's profiles fill band 0

- [ ] **BAND-014** [P1] Over-capacity band creation
    - Steps: Band size = 2 → Client with 3 profiles joins
    - Expected: Over-capacity band created, warning shown

## 5.3 Band Movement (Manual Reassignment)

- [ ] **BAND-020** [P0] Move single player to different band
    - Steps: Player alone in connection → Host moves player to band 1
    - Expected: Player moves successfully

- [ ] **BAND-021** [P0] Move connection group together
    - Steps: Client has 2 profiles in band 0 → Host moves one profile
    - Expected: Both profiles move to target band

- [ ] **BAND-022** [P1] Create new band via move
    - Steps: 4 players in band 0 → Host moves 1 to "new band"
    - Expected: New band created with player

- [ ] **BAND-023** [P1] Move to over-capacity band
    - Steps: Band size = 4, band 1 has 4 → Try move player to band 1
    - Expected: Move allowed, band shows over-capacity warning

- [ ] **BAND-024** [P1] Empty band cleanup
    - Steps: Band 1 has 1 player → Move player to band 0
    - Expected: Band 1 removed automatically

- [ ] **BAND-025** [P2] CanMovePlayerIndividually check
    - Steps: Player A alone, Player B in group of 2 → Check both
    - Expected: A: true, B: false

## 5.4 Band Names

- [ ] **BAND-030** [P1] Deterministic name generation
    - Steps: Create band with same seed twice
    - Expected: Same band name generated

- [ ] **BAND-031** [P0] Regenerate band name (host)
    - Steps: Host clicks regenerate on any band
    - Expected: New name generated, synced to all

- [ ] **BAND-032** [P1] Regenerate band name (band member)
    - Steps: Non-host client in band 1 clicks regenerate
    - Expected: New name generated for band 1 only

- [ ] **BAND-033** [P1] Cannot regenerate other band's name
    - Steps: Client in band 0 tries to regenerate band 1
    - Expected: Action blocked or rejected

- [ ] **BAND-034** [P1] Band name sync on join
    - Steps: Host regenerates name → New client joins
    - Expected: New client sees regenerated name

- [ ] **BAND-035** [P2] Name regeneration count sync
    - Steps: Regenerate 3 times → Check BandInfo.NameRegenerationCount
    - Expected: Count = 3, synced to all clients

## 5.5 Band Validation

- [ ] **BAND-040** [P0] ValidateBandConfiguration - valid
    - Steps: Band size = 4, all groups ≤ 4
    - Expected: Validation passes

- [ ] **BAND-041** [P0] ValidateBandConfiguration - invalid
    - Steps: Band size = 2, client has 3 profiles
    - Expected: Validation fails with clear message

- [ ] **BAND-042** [P0] ValidateForGameStart - over capacity
    - Steps: Band has 5 players, size = 4 → Try start game
    - Expected: Blocked with "move X player(s)" message

- [ ] **BAND-043** [P1] GetOverCapacityBands
    - Steps: Bands 0 and 2 over capacity
    - Expected: Returns [0, 2]

- [ ] **BAND-044** [P0] Block game start with over-capacity
    - Steps: Any band over capacity → Host clicks Start
    - Expected: Start blocked, error shown

## 5.6 Band Scoring (During Gameplay)

- [ ] **BAND-050** [P0] Band score aggregation
    - Steps: Band has 2 players: 10000 + 15000 → Check TotalScore
    - Expected: Band TotalScore = 25000

- [ ] **BAND-051** [P0] Score sync between bands
    - Steps: Play song with 2 bands → Check leaderboard
    - Expected: Both bands' scores visible

- [ ] **BAND-052** [P1] GetBandsByScore ordering
    - Steps: Band 0: 50k, Band 1: 75k → Query sorted
    - Expected: Returns [Band 1, Band 0]

- [ ] **BAND-053** [P1] ResetScores on new song
    - Steps: Play song, scores > 0 → Start new song
    - Expected: All band scores = 0

- [ ] **BAND-054** [P2] OnBandScoreUpdated event
    - Steps: Score changes → Check event
    - Expected: Event fires with correct bandId and score

## 5.7 Band Failure (No-Fail Off)

- [ ] **BAND-060** [P0] Single band fails
    - Steps: No-fail OFF → All players in band fail
    - Expected: Band marked HasFailed=true, event fires

- [ ] **BAND-061** [P1] Failed band spectates other band
    - Steps: Band 0 fails, Band 1 alive → Check Band 0 players
    - Expected: They spectate Band 1

- [ ] **BAND-062** [P0] All bands fail
    - Steps: All bands fail → Check OnAllBandsFailed
    - Expected: Event fires, song ends

- [ ] **BAND-063** [P1] GetAliveBandCount
    - Steps: 3 bands, 1 failed → Query
    - Expected: Returns 2

- [ ] **BAND-064** [P1] GetNextAliveBandToSpectate
    - Steps: Band 0 failed, Band 1 alive → Query from Band 0
    - Expected: Returns Band 1

- [ ] **BAND-065** [P1] ResetFailureStates on new song
    - Steps: Band failed in previous song → Start new song
    - Expected: HasFailed = false for all

## 5.8 Band Network Synchronization

- [ ] **BAND-070** [P0] BandAssignmentMessage on join
    - Steps: Client joins lobby → Check received data
    - Expected: Full band state synced (assignments, names, groups)

- [ ] **BAND-071** [P0] Band sync after movement
    - Steps: Host moves player → Check all clients
    - Expected: All clients see updated assignments

- [ ] **BAND-072** [P1] BandScoreUpdateMessage during play
    - Steps: Play song → Monitor network
    - Expected: Score updates sent periodically

- [ ] **BAND-073** [P1] BandFailedMessage broadcast
    - Steps: Band fails → Check other clients
    - Expected: All clients notified of failure

- [ ] **BAND-074** [P1] BandNameChangeMessage broadcast
    - Steps: Regenerate name → Check all clients
    - Expected: New name synced to all

- [ ] **BAND-075** [P1] Late joiner gets full band state
    - Steps: Lobby has bands configured → New client joins late
    - Expected: Client receives complete BandSyncData

## 5.9 Band UI Integration

- [ ] **BAND-080** [P0] Band headers in player list
    - Steps: 2 bands exist → View lobby
    - Expected: Band headers shown, players grouped

- [ ] **BAND-081** [P1] Regenerate button visibility
    - Steps: View own band header → View other band header
    - Expected: Button visible for own band only (or host)

- [ ] **BAND-082** [P1] Over-capacity warning display
    - Steps: Band over capacity → View band header
    - Expected: Warning indicator shown

- [ ] **BAND-083** [P1] Band score display during gameplay
    - Steps: Bands enabled → Play song
    - Expected: Band scores visible in HUD

- [ ] **BAND-084** [P1] Spectating banner when band fails
    - Steps: Own band fails → Check UI
    - Expected: "Watching: [Band Name]" banner shown

---

# ✅ 6. Ready State & Gameplay Flow Tests

## 6.1 Ready State Management

> **Related Changes:** `NetworkPlayerManager.SetPlayerReady`, `SittingOut` property

- [ ] **RDY-001** [P0] Player marks ready
    - Steps: Player clicks Ready
    - Expected: Ready indicator shown to all

- [ ] **RDY-002** [P0] Player unmarks ready
    - Steps: Ready player clicks Unready
    - Expected: Unready state shown to all

- [ ] **RDY-003** [P1] Sitting out state
    - Steps: Player marks as sitting out
    - Expected: SittingOut=true, shown to all

- [ ] **RDY-004** [P0] All players ready check
    - Steps: All players mark ready → System checks
    - Expected: AreAllPlayersReady() returns true

- [ ] **RDY-005** [P1] Reset ready states
    - Steps: All ready → Song ends → Return to lobby
    - Expected: All players reset to unready

## 6.2 Gameplay Phase Transitions

> **Related Changes:** `NetworkGameplayManager.cs`, `SessionPhase` fixes

- [ ] **GAME-001** [P0] Lobby → Song Selection
    - Steps: Host selects song
    - Expected: Phase changes to MusicLibrary/DifficultySelect

- [ ] **GAME-002** [P0] Countdown phase
    - Steps: All ready → Host starts song
    - Expected: Phase changes to Countdown

- [ ] **GAME-003** [P0] Playing phase
    - Steps: Countdown ends
    - Expected: Phase changes to PlayingSong

- [ ] **GAME-004** [P0] Score screen phase
    - Steps: Song ends
    - Expected: Phase changes to ScoreScreen

- [ ] **GAME-005** [P0] Return to lobby
    - Steps: Exit score screen
    - Expected: Phase returns to Lobby

## 6.3 Gameplay Synchronization

- [ ] **GAME-010** [P0] All players load same song
    - Steps: Host selects song all have → Start
    - Expected: All load and play same song

- [ ] **GAME-011** [P0] Score sync during gameplay
    - Steps: Play song → Check other players' scores
    - Expected: Scores update in real-time

- [ ] **GAME-012** [P1] Combo/streak sync
    - Steps: Build combo → Check remote view
    - Expected: Combo visible to other players

- [ ] **GAME-013** [P1] Star Power activation sync
    - Steps: Activate star power → Check remote view
    - Expected: SP visible to others

---

# ❌ 7. Network Error Handling Tests

## 7.1 Connection Errors

> **Related Changes:** `NetworkGuards.cs`, error handling improvements

- [ ] **ERR-001** [P0] Connection timeout
    - Steps: Try to connect to offline IP
    - Expected: Timeout after reasonable period, error shown

- [ ] **ERR-002** [P0] Connection refused
    - Steps: Connect to IP with no server
    - Expected: Clear error message, return to menu

- [ ] **ERR-003** [P0] Mid-game disconnect (client)
    - Steps: Client playing → Pull network cable
    - Expected: Client notified, returns to menu

- [ ] **ERR-004** [P0] Mid-game disconnect (host)
    - Steps: Playing → Host disconnects
    - Expected: All clients notified, return to menu

- [ ] **ERR-005** [P1] **NetworkGuards null checks**
    - Steps: Access networking before init
    - Expected: Guards prevent crash, log error

## 7.2 Recovery Scenarios

- [ ] **ERR-010** [P1] Reconnect after timeout
    - Steps: Disconnect occurs → Return to menu → Rejoin
    - Expected: Successful rejoin

- [ ] **ERR-011** [P1] Create new lobby after failed join
    - Steps: Join fails → Create own lobby
    - Expected: New lobby works correctly

- [ ] **ERR-012** [P1] Discovery after network restore
    - Steps: Disable network → Re-enable → Refresh discovery
    - Expected: Lobbies appear again

---

# ⚡ 8. Performance & Stress Tests

## 8.1 Load Testing

- [ ] **PERF-001** [P1] Maximum players
    - Steps: Fill lobby to max (4-8) → Play song
    - Expected: No significant performance drop

- [ ] **PERF-002** [P1] Long session stability
    - Steps: Stay in lobby 30+ minutes → Play multiple songs
    - Expected: No memory leaks, stable performance

- [ ] **PERF-003** [P2] Rapid player join/leave
    - Steps: Players join and leave repeatedly
    - Expected: No resource exhaustion

- [ ] **PERF-004** [P2] Many bands (8+ bands)
    - Steps: Band size = 2, 16 players → Play song
    - Expected: All bands tracked, UI responsive

## 8.2 Network Stress

- [ ] **PERF-010** [P1] High latency simulation
    - Steps: Add 200ms artificial latency → Play song
    - Expected: Gameplay remains playable

- [ ] **PERF-011** [P2] Packet loss simulation
    - Steps: Simulate 5% packet loss → Play song
    - Expected: Game handles gracefully

- [ ] **PERF-012** [P2] Discovery flood
    - Steps: 10+ lobbies on network → Client discovers
    - Expected: All lobbies shown, UI responsive

---

# 🔄 9. Regression Tests

## 9.1 Previously Fixed Issues

- [ ] **REG-001** [P0] No duplicate OnLobbyJoined
    - Related Fix: Duplicate event fix
    - Steps: Join lobby, check events
    - Expected: Single event fired

- [ ] **REG-002** [P0] No discovery deadlock
    - Related Fix: _lobbiesLock removal
    - Steps: Discovery + actions
    - Expected: No hang

- [ ] **REG-003** [P1] Debug traces removed
    - Related Fix: Stack trace cleanup
    - Steps: Play through all flows
    - Expected: No debug stack traces in logs

- [ ] **REG-004** [P2] Verbose logs use NetworkLogger
    - Related Fix: Log conversion
    - Steps: Enable verbose, check logs
    - Expected: Proper log levels

## 9.2 Removed/Obsolete Code

- [ ] **REG-010** [P1] No calls to removed methods
    - Steps: Search codebase
    - Expected: No references to obsolete methods

- [ ] **REG-011** [P1] New managers integrated
    - Steps: Check adapter initialization
    - Expected: Managers created and used

---

# 🔮 10. Edge Cases & Boundary Conditions

## 10.1 Timing Edge Cases

- [ ] **EDGE-001** [P1] Join during countdown
    - Steps: Host starts countdown → Client joins
    - Expected: Client either joins next song or waits

- [ ] **EDGE-002** [P1] Leave during countdown
    - Steps: Countdown active → Client leaves
    - Expected: Clean departure, others continue

- [ ] **EDGE-003** [P2] Ready/unready spam
    - Steps: Toggle ready 20 times rapidly
    - Expected: State eventually consistent

- [ ] **EDGE-004** [P1] Song select during join
    - Steps: Host selecting song → Client joins
    - Expected: Client sees current selection

## 10.2 State Edge Cases

- [ ] **EDGE-010** [P2] Empty lobby (dedicated server)
    - Steps: Create dedicated server lobby → No players
    - Expected: Lobby remains stable

- [ ] **EDGE-011** [P2] All players sitting out
    - Steps: All mark sitting out → Try to start
    - Expected: Prevented or handled gracefully

- [ ] **EDGE-012** [P2] Host promotion (dedicated)
    - Steps: Dedicated server → First player becomes host → Host leaves
    - Expected: Second player promoted

- [ ] **EDGE-013** [P2] Band movement during countdown
    - Steps: Countdown started → Try to move player
    - Expected: Movement blocked or handled

- [ ] **EDGE-014** [P2] Band size change mid-lobby
    - Steps: Players in bands → Change band size
    - Expected: Players reassigned correctly

## 10.3 Data Boundary Cases

- [ ] **EDGE-020** [P2] Empty lobby name
    - Steps: Try create lobby with no name
    - Expected: Validation prevents or uses default

- [ ] **EDGE-021** [P2] Very long lobby name
    - Steps: 256+ character name
    - Expected: Truncated or rejected

- [ ] **EDGE-022** [P2] Special characters in name
    - Steps: Name with unicode/emoji
    - Expected: Displayed correctly or sanitized

- [ ] **EDGE-023** [P2] Max player count boundary
    - Steps: Set max=1, try to join
    - Expected: Only host allowed

- [ ] **EDGE-024** [P2] Band size = 1
    - Steps: Set band size = 1 → Multiple players join
    - Expected: Each player in own band

- [ ] **EDGE-025** [P2] Band size > max players
    - Steps: Band size = 8, max players = 4
    - Expected: Works, all in one band

---

# 📊 Test Execution Summary

## Pre-Release Checklist

- [ ] All P0 tests pass
- [ ] All P1 tests pass
- [ ] No critical P2 failures
- [ ] Performance benchmarks met
- [ ] No memory leaks detected
- [ ] Logs are clean (no unexpected errors)

## Sign-Off Criteria

| Criteria | Requirement | Status |
|----------|-------------|--------|
| P0 Tests | 100% pass | ⬜ |
| P1 Tests | 95%+ pass | ⬜ |
| P2 Tests | 80%+ pass | ⬜ |
| Performance | < 5% regression | ⬜ |
| Stability | No crashes in 1-hour session | ⬜ |

---

# 📝 Test Notes

Use this section to record observations, bugs found, or additional notes during testing:

## Session 1
**Date:** _______________  
**Tester:** _______________

**Notes:**


---

## Session 2
**Date:** _______________  
**Tester:** _______________

**Notes:**


---

## Bugs Found

| Bug ID | Test Case | Description | Severity | Status |
|--------|-----------|-------------|----------|--------|
| | | | | |
| | | | | |
| | | | | |

---

# 📎 Appendix: Test Data

## Sample Lobby Configurations

**Public Lobby:**
- Name: "Test Public Lobby"
- Max Players: 4
- Privacy: Public
- Password: None
- Band Size: 0

**Private Lobby:**
- Name: "Test Private Lobby"
- Max Players: 4
- Privacy: Private
- Password: "test123"
- Band Size: 0

**Band Mode Lobby:**
- Name: "Band Battle"
- Max Players: 8
- Privacy: Public
- Band Size: 4

## Test Player Profiles

| Profile | Instrument | Difficulty | Band Scenario |
|---------|------------|------------|---------------|
| TestHost | Lead Guitar | Expert | Band 0 |
| TestClient1 | Drums | Hard | Band 0 |
| TestClient2 | Bass | Medium | Band 1 |
| TestClient3 | Vocals | Easy | Band 1 |

## Band Test Scenarios

| Scenario | Band Size | Players | Expected Bands |
|----------|-----------|---------|----------------|
| Disabled | 0 | Any | All in Band 0 |
| 2v2 | 2 | 4 | Band 0: 2, Band 1: 2 |
| 4v4 | 4 | 8 | Band 0: 4, Band 1: 4 |
| Uneven | 4 | 6 | Band 0: 4, Band 1: 2 |
