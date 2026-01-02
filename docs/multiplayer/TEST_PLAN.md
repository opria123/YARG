# Multiplayer Networking Test Plan

**Version:** 1.1  
**Date:** January 2026  
**Scope:** All changes from multiplayer networking code review and short-term improvements

---

## Table of Contents

1. [Test Environment Setup](#1-test-environment-setup)
2. [Lobby Discovery Tests](#2-lobby-discovery-tests)
3. [Lobby Lifecycle Tests](#3-lobby-lifecycle-tests)
4. [Player Management Tests](#4-player-management-tests)
5. [Band System Tests](#5-band-system-tests)
6. [Ready State & Gameplay Flow Tests](#6-ready-state--gameplay-flow-tests)
7. [Network Error Handling Tests](#7-network-error-handling-tests)
8. [Performance & Stress Tests](#8-performance--stress-tests)
9. [Regression Tests](#9-regression-tests)
10. [Edge Cases & Boundary Conditions](#10-edge-cases--boundary-conditions)

---

## 1. Test Environment Setup

### Required Configuration

| Setup | Description |
|-------|-------------|
| **Minimum Machines** | 2 (can use ParrelSync for single-machine testing) |
| **Recommended Machines** | 3+ for multi-client scenarios |
| **Network Configurations** | LAN, localhost, different subnets (if testing WAN) |
| **Unity Editor** | For debugging and log inspection |
| **Build** | Standalone builds for realistic testing |

### Pre-Test Checklist

- [ ] Clear all PlayerPrefs/settings from previous sessions
- [ ] Ensure no other YARG instances are running
- [ ] Verify firewall allows UDP on port 7777 (or configured port)
- [ ] Have Task Manager ready (in case of hangs)
- [ ] Enable verbose logging for detailed diagnostics

---

## 2. Lobby Discovery Tests

### 2.1 LAN Discovery - Basic Flow

**Related Changes:** `LiteNetDiscovery.cs` deadlock fix, removed redundant `_lobbiesLock`

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| DISC-001 | Host creates discoverable lobby | 1. Host creates public lobby<br>2. Client opens lobby browser | Client sees lobby in list within 5 seconds | P0 |
| DISC-002 | Lobby info accuracy | 1. Host creates lobby with specific settings<br>2. Client views lobby in browser | All settings (name, players, password status) match | P0 |
| DISC-003 | **No hang on discovery** | 1. Client opens lobby browser<br>2. Discovery ping sent<br>3. Host responds | Client UI remains responsive, no freeze | P0 |
| DISC-004 | Multiple lobbies discovered | 1. Start 3 hosts with different lobbies<br>2. Client opens browser | All 3 lobbies appear in list | P1 |
| DISC-005 | Lobby disappears when closed | 1. Host creates lobby<br>2. Client sees it<br>3. Host closes lobby | Lobby removed from list within 15 seconds | P1 |
| DISC-006 | Rapid discovery refresh | 1. Client spams refresh button 10 times quickly | No crashes, UI remains responsive | P1 |

### 2.2 Direct Connect Discovery

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| DISC-010 | Direct connect by IP | 1. Host creates lobby<br>2. Client enters host IP:port directly | Client connects successfully | P0 |
| DISC-011 | Direct connect invalid IP | 1. Client enters non-existent IP<br>2. Attempts connect | Graceful timeout with error message | P1 |
| DISC-012 | Direct connect wrong port | 1. Host on port 7777<br>2. Client connects to port 7778 | Graceful failure, no hang | P1 |

### 2.3 Discovery Thread Safety (Deadlock Prevention)

**Related Changes:** Removed nested locking in `ProcessDiscoveryResponse`, removed `_lobbiesLock`

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| DISC-020 | Concurrent discovery operations | 1. Start discovery<br>2. While discovering, clear lobbies<br>3. Refresh again | No deadlock, operations complete | P0 |
| DISC-021 | Discovery during lobby join | 1. Start discovery<br>2. Click join on a lobby<br>3. Discovery continues in background | Both operations succeed without blocking | P1 |
| DISC-022 | Rapid start/stop discovery | 1. Start discovery<br>2. Stop immediately<br>3. Start again<br>4. Repeat 20 times | No resource leaks, no hangs | P1 |

---

## 3. Lobby Lifecycle Tests

### 3.1 Lobby Creation

**Related Changes:** `NetworkLobbyManager.cs`, `CreateLobbyInfo` fixes (SessionType, Password)

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| LOB-001 | Create public lobby | 1. Click Create Lobby<br>2. Set to Public<br>3. Confirm | Lobby created, host in lobby | P0 |
| LOB-002 | Create private (password) lobby | 1. Create lobby with password "test123" | Lobby shows HasPassword=true | P0 |
| LOB-003 | Create unlisted lobby | 1. Create lobby with Unlisted mode | Lobby not visible in discovery | P1 |
| LOB-004 | Lobby code generation (Lobby mode) | 1. Create lobby in "Lobby" session type | 6-character lobby code generated | P1 |
| LOB-005 | Server mode (no lobby code) | 1. Create lobby in "Server" session type | No lobby code, IsServer=true | P1 |

### 3.2 Lobby Joining

**Related Changes:** Fixed duplicate `OnLobbyJoined` bug

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| LOB-010 | Join public lobby | 1. Host creates public lobby<br>2. Client joins | Client enters lobby, sees host | P0 |
| LOB-011 | Join password-protected lobby | 1. Host creates lobby with password<br>2. Client enters correct password | Client joins successfully | P0 |
| LOB-012 | Join with wrong password | 1. Host creates lobby with password<br>2. Client enters wrong password | Join rejected with clear error | P0 |
| LOB-013 | **No duplicate OnLobbyJoined** | 1. Client joins lobby<br>2. Check logs/event handlers | OnLobbyJoined fires exactly once | P0 |
| LOB-014 | Join full lobby | 1. Host creates 2-player lobby<br>2. Client 1 joins<br>3. Client 2 tries to join | Client 2 rejected with "lobby full" | P1 |
| LOB-015 | Rejoin after disconnect | 1. Client joins<br>2. Client disconnects<br>3. Client rejoins | Successful rejoin, clean state | P1 |

### 3.3 Lobby Leaving

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| LOB-020 | Client leaves lobby | 1. Client in lobby<br>2. Client clicks Leave | Client returns to menu, host sees departure | P0 |
| LOB-021 | Host leaves (closes lobby) | 1. Host with clients<br>2. Host leaves | All clients disconnected with message | P0 |
| LOB-022 | Client force-quit | 1. Client in lobby<br>2. Client Alt+F4s | Host sees client leave within timeout | P1 |
| LOB-023 | Host force-quit | 1. Host with clients<br>2. Host Alt+F4s | Clients detect disconnect, return to menu | P1 |

---

## 4. Player Management Tests

### 4.1 Player Tracking

**Related Changes:** `NetworkPlayerManager.cs`, `NetworkPlayerExtensions.cs`

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| PLR-001 | Host player added on create | 1. Host creates lobby | Host appears in player list as host | P0 |
| PLR-002 | Client player added on join | 1. Client joins lobby | Client appears in player list | P0 |
| PLR-003 | Multiple local players | 1. Host adds 2 local profiles<br>2. Both join lobby | Both players tracked correctly | P1 |
| PLR-004 | Player name display | 1. Set profile name "TestPlayer"<br>2. Join lobby | Name shows correctly to all | P0 |
| PLR-005 | Player instrument display | 1. Select guitar<br>2. Join lobby | Instrument visible to all players | P0 |

### 4.2 Player Extensions (LINQ Helpers)

**Related Changes:** `NetworkPlayerExtensions.cs` - `SittingOut` property fix

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| PLR-010 | GetAllPlayersSafe | 1. 3 players in lobby<br>2. Query all players | Returns all 3 players | P1 |
| PLR-011 | GetLocalPlayers | 1. Host with 2 local, 1 remote<br>2. Query local | Returns 2 local players | P1 |
| PLR-012 | GetRemotePlayers | 1. Host with 2 local, 1 remote<br>2. Query remote | Returns 1 remote player | P1 |
| PLR-013 | GetSittingOutPlayers | 1. 1 player sitting out<br>2. Query sitting out | Returns correct player | P1 |
| PLR-014 | GetActivePlayers | 1. 2 ready, 1 sitting out<br>2. Query active | Returns 2 active players | P1 |
| PLR-015 | Null safety | 1. Query with null dictionary | Returns empty, no exception | P1 |

### 4.3 Player State Changes

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| PLR-020 | Player changes instrument | 1. Client changes from guitar to drums | All players see update | P0 |
| PLR-021 | Player changes difficulty | 1. Client changes from Medium to Expert | All players see update | P0 |
| PLR-022 | Player changes name mid-lobby | 1. Client renames profile | All players see new name | P2 |

---

## 5. Band System Tests

### 5.1 Band Initialization & Configuration

**Related Changes:** `BandManager.cs`, `NetworkGuards.TryGetBandManager`

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| BAND-001 | Band system disabled (size=0) | 1. Create lobby with band size 0<br>2. Add 8 players | All players in single band (band 0) | P0 |
| BAND-002 | Band system enabled (size=4) | 1. Create lobby with band size 4<br>2. Add 8 players | Players split into 2 bands of 4 | P0 |
| BAND-003 | Band size validation on join | 1. Band size = 2<br>2. Client with 3 local profiles tries to join | Error shown: increase band size or remove profiles | P0 |
| BAND-004 | Band initialization persistence | 1. Initialize band system<br>2. Change band size<br>3. Check lobby seed | Seed preserved, players reassigned | P1 |
| BAND-005 | IsBandSystemActive check | 1. Band size = 4, 4 players in 1 band<br>2. Check IsBandSystemActive | Returns false (only 1 band exists) | P1 |
| BAND-006 | AreBandsEnabled check | 1. Band size > 0<br>2. Check AreBandsEnabled | Returns true even with single band | P1 |

### 5.2 Band Assignment (Auto-Assignment)

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| BAND-010 | First player gets band 0 | 1. Band size = 4<br>2. Host joins | Host in band 0 | P0 |
| BAND-011 | Connection group stays together | 1. Client has 2 local profiles<br>2. Client joins | Both profiles in same band | P0 |
| BAND-012 | Auto-create new band when full | 1. Band 0 has 4 players (full)<br>2. New client joins | New client in band 1 | P0 |
| BAND-013 | Fill existing band before creating new | 1. Band size = 4, Band 0 has 2 players<br>2. Client with 2 profiles joins | Client's profiles fill band 0 | P1 |
| BAND-014 | Over-capacity band creation | 1. Band size = 2<br>2. Client with 3 profiles joins | Over-capacity band created, warning shown | P1 |

### 5.3 Band Movement (Manual Reassignment)

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| BAND-020 | Move single player to different band | 1. Player alone in connection<br>2. Host moves player to band 1 | Player moves successfully | P0 |
| BAND-021 | Move connection group together | 1. Client has 2 profiles in band 0<br>2. Host moves one profile | Both profiles move to target band | P0 |
| BAND-022 | Create new band via move | 1. 4 players in band 0<br>2. Host moves 1 to "new band" | New band created with player | P1 |
| BAND-023 | Move to over-capacity band | 1. Band size = 4, band 1 has 4<br>2. Try move player to band 1 | Move allowed, band shows over-capacity warning | P1 |
| BAND-024 | Empty band cleanup | 1. Band 1 has 1 player<br>2. Move player to band 0 | Band 1 removed automatically | P1 |
| BAND-025 | CanMovePlayerIndividually check | 1. Player A alone, Player B in group of 2<br>2. Check both | A: true, B: false | P2 |

### 5.4 Band Names

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| BAND-030 | Deterministic name generation | 1. Create band with same seed twice | Same band name generated | P1 |
| BAND-031 | Regenerate band name (host) | 1. Host clicks regenerate on any band | New name generated, synced to all | P0 |
| BAND-032 | Regenerate band name (band member) | 1. Non-host client in band 1 clicks regenerate | New name generated for band 1 only | P1 |
| BAND-033 | Cannot regenerate other band's name | 1. Client in band 0 tries to regenerate band 1 | Action blocked or rejected | P1 |
| BAND-034 | Band name sync on join | 1. Host regenerates name<br>2. New client joins | New client sees regenerated name | P1 |
| BAND-035 | Name regeneration count sync | 1. Regenerate 3 times<br>2. Check BandInfo.NameRegenerationCount | Count = 3, synced to all clients | P2 |

### 5.5 Band Validation

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| BAND-040 | ValidateBandConfiguration - valid | 1. Band size = 4, all groups ≤ 4 | Validation passes | P0 |
| BAND-041 | ValidateBandConfiguration - invalid | 1. Band size = 2, client has 3 profiles | Validation fails with clear message | P0 |
| BAND-042 | ValidateForGameStart - over capacity | 1. Band has 5 players, size = 4<br>2. Try start game | Blocked with "move X player(s)" message | P0 |
| BAND-043 | GetOverCapacityBands | 1. Bands 0 and 2 over capacity | Returns [0, 2] | P1 |
| BAND-044 | Block game start with over-capacity | 1. Any band over capacity<br>2. Host clicks Start | Start blocked, error shown | P0 |

### 5.6 Band Scoring (During Gameplay)

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| BAND-050 | Band score aggregation | 1. Band has 2 players: 10000 + 15000<br>2. Check TotalScore | Band TotalScore = 25000 | P0 |
| BAND-051 | Score sync between bands | 1. Play song with 2 bands<br>2. Check leaderboard | Both bands' scores visible | P0 |
| BAND-052 | GetBandsByScore ordering | 1. Band 0: 50k, Band 1: 75k<br>2. Query sorted | Returns [Band 1, Band 0] | P1 |
| BAND-053 | ResetScores on new song | 1. Play song, scores > 0<br>2. Start new song | All band scores = 0 | P1 |
| BAND-054 | OnBandScoreUpdated event | 1. Score changes<br>2. Check event | Event fires with correct bandId and score | P2 |

### 5.7 Band Failure (No-Fail Off)

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| BAND-060 | Single band fails | 1. No-fail OFF<br>2. All players in band fail | Band marked HasFailed=true, event fires | P0 |
| BAND-061 | Failed band spectates other band | 1. Band 0 fails, Band 1 alive<br>2. Check Band 0 players | They spectate Band 1 | P1 |
| BAND-062 | All bands fail | 1. All bands fail<br>2. Check OnAllBandsFailed | Event fires, song ends | P0 |
| BAND-063 | GetAliveBandCount | 1. 3 bands, 1 failed<br>2. Query | Returns 2 | P1 |
| BAND-064 | GetNextAliveBandToSpectate | 1. Band 0 failed, Band 1 alive<br>2. Query from Band 0 | Returns Band 1 | P1 |
| BAND-065 | ResetFailureStates on new song | 1. Band failed in previous song<br>2. Start new song | HasFailed = false for all | P1 |

### 5.8 Band Network Synchronization

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| BAND-070 | BandAssignmentMessage on join | 1. Client joins lobby<br>2. Check received data | Full band state synced (assignments, names, groups) | P0 |
| BAND-071 | Band sync after movement | 1. Host moves player<br>2. Check all clients | All clients see updated assignments | P0 |
| BAND-072 | BandScoreUpdateMessage during play | 1. Play song<br>2. Monitor network | Score updates sent periodically | P1 |
| BAND-073 | BandFailedMessage broadcast | 1. Band fails<br>2. Check other clients | All clients notified of failure | P1 |
| BAND-074 | BandNameChangeMessage broadcast | 1. Regenerate name<br>2. Check all clients | New name synced to all | P1 |
| BAND-075 | Late joiner gets full band state | 1. Lobby has bands configured<br>2. New client joins late | Client receives complete BandSyncData | P1 |

### 5.9 Band UI Integration

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| BAND-080 | Band headers in player list | 1. 2 bands exist<br>2. View lobby | Band headers shown, players grouped | P0 |
| BAND-081 | Regenerate button visibility | 1. View own band header<br>2. View other band header | Button visible for own band only (or host) | P1 |
| BAND-082 | Over-capacity warning display | 1. Band over capacity<br>2. View band header | Warning indicator shown | P1 |
| BAND-083 | Band score display during gameplay | 1. Bands enabled<br>2. Play song | Band scores visible in HUD | P1 |
| BAND-084 | Spectating banner when band fails | 1. Own band fails<br>2. Check UI | "Watching: [Band Name]" banner shown | P1 |

---

## 6. Ready State & Gameplay Flow Tests

### 6.1 Ready State Management

**Related Changes:** `NetworkPlayerManager.SetPlayerReady`, `SittingOut` property

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| RDY-001 | Player marks ready | 1. Player clicks Ready | Ready indicator shown to all | P0 |
| RDY-002 | Player unmarks ready | 1. Ready player clicks Unready | Unready state shown to all | P0 |
| RDY-003 | Sitting out state | 1. Player marks as sitting out | SittingOut=true, shown to all | P1 |
| RDY-004 | All players ready check | 1. All players mark ready<br>2. System checks | AreAllPlayersReady() returns true | P0 |
| RDY-005 | Reset ready states | 1. All ready<br>2. Song ends<br>3. Return to lobby | All players reset to unready | P1 |

### 6.2 Gameplay Phase Transitions

**Related Changes:** `NetworkGameplayManager.cs`, `SessionPhase` fixes

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| GAME-001 | Lobby → Song Selection | 1. Host selects song | Phase changes to MusicLibrary/DifficultySelect | P0 |
| GAME-002 | Countdown phase | 1. All ready<br>2. Host starts song | Phase changes to Countdown | P0 |
| GAME-003 | Playing phase | 1. Countdown ends | Phase changes to PlayingSong | P0 |
| GAME-004 | Score screen phase | 1. Song ends | Phase changes to ScoreScreen | P0 |
| GAME-005 | Return to lobby | 1. Exit score screen | Phase returns to Lobby | P0 |

### 6.3 Gameplay Synchronization

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| GAME-010 | All players load same song | 1. Host selects song all have<br>2. Start | All load and play same song | P0 |
| GAME-011 | Score sync during gameplay | 1. Play song<br>2. Check other players' scores | Scores update in real-time | P0 |
| GAME-012 | Combo/streak sync | 1. Build combo<br>2. Check remote view | Combo visible to other players | P1 |
| GAME-013 | Star Power activation sync | 1. Activate star power<br>2. Check remote view | SP visible to others | P1 |

---

## 7. Network Error Handling Tests

### 7.1 Connection Errors

**Related Changes:** `NetworkGuards.cs`, error handling improvements

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| ERR-001 | Connection timeout | 1. Try to connect to offline IP | Timeout after reasonable period, error shown | P0 |
| ERR-002 | Connection refused | 1. Connect to IP with no server | Clear error message, return to menu | P0 |
| ERR-003 | Mid-game disconnect (client) | 1. Client playing<br>2. Pull network cable | Client notified, returns to menu | P0 |
| ERR-004 | Mid-game disconnect (host) | 1. Playing<br>2. Host disconnects | All clients notified, return to menu | P0 |
| ERR-005 | **NetworkGuards null checks** | 1. Access networking before init | Guards prevent crash, log error | P1 |

### 7.2 Recovery Scenarios

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| ERR-010 | Reconnect after timeout | 1. Disconnect occurs<br>2. Return to menu<br>3. Rejoin | Successful rejoin | P1 |
| ERR-011 | Create new lobby after failed join | 1. Join fails<br>2. Create own lobby | New lobby works correctly | P1 |
| ERR-012 | Discovery after network restore | 1. Disable network<br>2. Re-enable<br>3. Refresh discovery | Lobbies appear again | P1 |

---

## 8. Performance & Stress Tests

### 8.1 Load Testing

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| PERF-001 | Maximum players | 1. Fill lobby to max (4-8)<br>2. Play song | No significant performance drop | P1 |
| PERF-002 | Long session stability | 1. Stay in lobby 30+ minutes<br>2. Play multiple songs | No memory leaks, stable performance | P1 |
| PERF-003 | Rapid player join/leave | 1. Players join and leave repeatedly | No resource exhaustion | P2 |
| PERF-004 | Many bands (8+ bands) | 1. Band size = 2, 16 players<br>2. Play song | All bands tracked, UI responsive | P2 |

### 8.2 Network Stress

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| PERF-010 | High latency simulation | 1. Add 200ms artificial latency<br>2. Play song | Gameplay remains playable | P1 |
| PERF-011 | Packet loss simulation | 1. Simulate 5% packet loss<br>2. Play song | Game handles gracefully | P2 |
| PERF-012 | Discovery flood | 1. 10+ lobbies on network<br>2. Client discovers | All lobbies shown, UI responsive | P2 |

---

## 9. Regression Tests

### 9.1 Previously Fixed Issues

| Test ID | Test Case | Related Fix | Steps | Expected Result | Priority |
|---------|-----------|-------------|-------|-----------------|----------|
| REG-001 | No duplicate OnLobbyJoined | Duplicate event fix | Join lobby, check events | Single event fired | P0 |
| REG-002 | No discovery deadlock | _lobbiesLock removal | Discovery + actions | No hang | P0 |
| REG-003 | Debug traces removed | Stack trace cleanup | Play through all flows | No debug stack traces in logs | P1 |
| REG-004 | Verbose logs use NetworkLogger | Log conversion | Enable verbose, check logs | Proper log levels | P2 |

### 9.2 Removed/Obsolete Code

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| REG-010 | No calls to removed methods | Search codebase | No references to obsolete methods | P1 |
| REG-011 | New managers integrated | Check adapter initialization | Managers created and used | P1 |

---

## 10. Edge Cases & Boundary Conditions

### 10.1 Timing Edge Cases

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| EDGE-001 | Join during countdown | 1. Host starts countdown<br>2. Client joins | Client either joins next song or waits | P1 |
| EDGE-002 | Leave during countdown | 1. Countdown active<br>2. Client leaves | Clean departure, others continue | P1 |
| EDGE-003 | Ready/unready spam | 1. Toggle ready 20 times rapidly | State eventually consistent | P2 |
| EDGE-004 | Song select during join | 1. Host selecting song<br>2. Client joins | Client sees current selection | P1 |

### 10.2 State Edge Cases

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| EDGE-010 | Empty lobby (dedicated server) | 1. Create dedicated server lobby<br>2. No players | Lobby remains stable | P2 |
| EDGE-011 | All players sitting out | 1. All mark sitting out<br>2. Try to start | Prevented or handled gracefully | P2 |
| EDGE-012 | Host promotion (dedicated) | 1. Dedicated server<br>2. First player becomes host<br>3. Host leaves | Second player promoted | P2 |
| EDGE-013 | Band movement during countdown | 1. Countdown started<br>2. Try to move player | Movement blocked or handled | P2 |
| EDGE-014 | Band size change mid-lobby | 1. Players in bands<br>2. Change band size | Players reassigned correctly | P2 |

### 10.3 Data Boundary Cases

| Test ID | Test Case | Steps | Expected Result | Priority |
|---------|-----------|-------|-----------------|----------|
| EDGE-020 | Empty lobby name | 1. Try create lobby with no name | Validation prevents or uses default | P2 |
| EDGE-021 | Very long lobby name | 1. 256+ character name | Truncated or rejected | P2 |
| EDGE-022 | Special characters in name | 1. Name with unicode/emoji | Displayed correctly or sanitized | P2 |
| EDGE-023 | Max player count boundary | 1. Set max=1, try to join | Only host allowed | P2 |
| EDGE-024 | Band size = 1 | 1. Set band size = 1<br>2. Multiple players join | Each player in own band | P2 |
| EDGE-025 | Band size > max players | 1. Band size = 8, max players = 4 | Works, all in one band | P2 |

---

## Test Execution Checklist

### Pre-Release Checklist

- [ ] All P0 tests pass
- [ ] All P1 tests pass
- [ ] No critical P2 failures
- [ ] Performance benchmarks met
- [ ] No memory leaks detected
- [ ] Logs are clean (no unexpected errors)

### Sign-Off Criteria

| Criteria | Requirement |
|----------|-------------|
| P0 Tests | 100% pass |
| P1 Tests | 95%+ pass |
| P2 Tests | 80%+ pass |
| Performance | < 5% regression |
| Stability | No crashes in 1-hour session |

---

## Appendix: Test Data

### Sample Lobby Configurations

```yaml
Public Lobby:
  name: "Test Public Lobby"
  maxPlayers: 4
  privacyMode: Public
  password: null
  sessionType: Lobby
  bandSize: 0  # Bands disabled

Private Lobby:
  name: "Test Private Lobby"
  maxPlayers: 4
  privacyMode: Private
  password: "test123"
  sessionType: Server
  bandSize: 0

Band Mode Lobby:
  name: "Band Battle"
  maxPlayers: 8
  privacyMode: Public
  bandSize: 4  # 2 bands of 4

Dedicated Server:
  name: "Test Dedicated"
  maxPlayers: 8
  privacyMode: Public
  isDedicatedServer: true
  bandSize: 4
```

### Test Player Profiles

| Profile | Instrument | Difficulty | Band Scenario |
|---------|------------|------------|---------------|
| TestHost | Lead Guitar | Expert | Band 0 |
| TestClient1 | Drums | Hard | Band 0 |
| TestClient2 | Bass | Medium | Band 1 |
| TestClient3 | Vocals | Easy | Band 1 |
| LocalProfile1 | Guitar | Expert | Same connection |
| LocalProfile2 | Keys | Hard | Same connection |

### Band Test Scenarios

| Scenario | Band Size | Players | Expected Bands |
|----------|-----------|---------|----------------|
| Disabled | 0 | Any | All in Band 0 |
| 2v2 | 2 | 4 | Band 0: 2, Band 1: 2 |
| 4v4 | 4 | 8 | Band 0: 4, Band 1: 4 |
| Uneven | 4 | 6 | Band 0: 4, Band 1: 2 |
| Over-capacity | 2 | 3 (1 client) | Band 0: 3 (warning) |

---

## Change Log

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | Jan 2026 | Initial test plan for networking code review changes |
| 1.1 | Jan 2026 | Added comprehensive Band System tests (Section 5) |
