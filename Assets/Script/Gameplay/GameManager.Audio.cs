using System;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using DG.Tweening.Core;
using DG.Tweening.Plugins.Options;
using UnityEngine;
using YARG.Core.Audio;
using YARG.Networking.Abstraction;
using YARG.Networking.Bands;
using YARG.Networking.Gameplay;
using YARG.Playback;
using YARG.Settings;

namespace YARG.Gameplay
{
    public partial class GameManager
    {
        private const double DEFAULT_VOLUME = 1.0;
        public class StemState
        {
            private SongStem _stem;
            public double Volume => GetVolumeSetting();
            public int Total;
            public int Audible;
            public int ReverbCount;
            public float WhammyPitch;

            public StemState(SongStem stem)
            {
                _stem = stem;
            }

            public double SetMute(bool muted)
            {
                if (muted)
                {
                    --Audible;
                }
                else if (Audible < Total)
                {
                    ++Audible;
                }

                return Volume * Audible / Total;
            }

            public bool SetReverb(bool reverb)
            {
                if (reverb)
                {
                    ++ReverbCount;
                }
                else if (ReverbCount > 0)
                {
                    --ReverbCount;
                }
                return ReverbCount > 0;
            }

            public float SetWhammyPitch(float percent)
            {
                // TODO: Would be nice to handle multiple inputs
                // but for now last one wins
                WhammyPitch = Mathf.Clamp01(percent);
                return WhammyPitch;
            }

            private double GetVolumeSetting()
            {
                return _stem switch
                {
                    SongStem.Guitar => SettingsManager.Settings.GuitarVolume.Value,
                    SongStem.Rhythm => SettingsManager.Settings.RhythmVolume.Value,
                    SongStem.Bass   => SettingsManager.Settings.BassVolume.Value,
                    SongStem.Keys   => SettingsManager.Settings.KeysVolume.Value,
                    SongStem.Drums
                        or SongStem.Drums1
                        or SongStem.Drums2
                        or SongStem.Drums3
                        or SongStem.Drums4
                        => SettingsManager.Settings.DrumsVolume.Value,
                    SongStem.Vocals
                        or SongStem.Vocals1
                        or SongStem.Vocals2
                        => SettingsManager.Settings.VocalsVolume.Value,
                    SongStem.Song    => SettingsManager.Settings.SongVolume.Value,
                    SongStem.Crowd   => SettingsManager.Settings.CrowdVolume.Value,
                    SongStem.Sfx     => SettingsManager.Settings.SfxVolume.Value,
                    SongStem.DrumSfx => SettingsManager.Settings.DrumSfxVolume.Value,
                    _                => DEFAULT_VOLUME
                };
            }
        }

        private readonly Dictionary<SongStem, StemState>        _stemStates = new();
        private          SongStem                               _backgroundStem;
        private          TweenerCore<double, double, NoOptions> _volumeTween;

        private void LoadAudio()
        {
            _stemStates.Clear();
            _mixer = Song.LoadAudio(GlobalVariables.State.SongSpeed, DEFAULT_VOLUME);
            if (_mixer == null)
            {
                _loadState = LoadFailureState.Error;
                _loadFailureMessage = "Failed to load audio!";
                return;
            }

            _backgroundStem = SongStem.Song;
            foreach (var channel in _mixer.Channels)
            {
                var stemState = new StemState(channel.Stem);
                switch (channel.Stem)
                {
                    case SongStem.Drums:
                    case SongStem.Drums1:
                    case SongStem.Drums2:
                    case SongStem.Drums3:
                    case SongStem.Drums4:
                        _stemStates.TryAdd(SongStem.Drums, stemState);
                        break;
                    case SongStem.Vocals:
                    case SongStem.Vocals1:
                    case SongStem.Vocals2:
                        _stemStates.TryAdd(SongStem.Vocals, stemState);
                        break;
                    default:
                        _stemStates.Add(channel.Stem, stemState);
                        break;
                }
            }

            _backgroundStem = _stemStates.Count > 1 ? SongStem.Song : _stemStates.First().Key;
        }

        public void ChangeStarPowerStatus(bool active)
        {
            ChangeStarPowerStatus(active, isFromRemotePlayer: false);
        }
        
        /// <summary>
        /// Changes Star Power status and optionally triggers revival logic.
        /// </summary>
        /// <param name="active">Whether Star Power is now active.</param>
        /// <param name="isFromRemotePlayer">If true, this is from a remote/spectator player and should NOT trigger revival.</param>
        public void ChangeStarPowerStatus(bool active, bool isFromRemotePlayer)
        {
            if (SettingsManager.Settings.UseCrowdFx.Value == CrowdFxMode.Disabled)
                return;

            StarPowerActivations += active ? 1 : -1;
            if (StarPowerActivations < 0)
                StarPowerActivations = 0;
            
            // When Star Power is activated, try to revive any failed players
            // This works when No Fail mode is OFF (players can actually fail)
            // Works for both local and multiplayer sessions
            // In band mode, only revives players in the same band
            // IMPORTANT: Do NOT trigger revival from remote/spectator players - their Star Power
            // activations should only affect visuals/audio, not game mechanics like revival.
            // Revival from remote players' Star Power is handled separately via the network state.
            if (active && !IsNoFailActive && !IsPractice && !isFromRemotePlayer)
            {
                TryReviveFailedPlayersWithStarPower();
            }
        }
        
        /// <summary>
        /// Attempts to revive failed players when Star Power is activated.
        /// Works when No Fail mode is OFF for both local and multiplayer sessions.
        /// When band mode is active, only revives players in the same band.
        /// </summary>
        private void TryReviveFailedPlayersWithStarPower()
        {
            // Check if there are any failed players that can be revived
            if (!EngineManager.HasAnyFailedPlayer())
            {
                return;
            }
            
            // Build filter for band-aware revival
            Func<int, bool> revivalFilter = null;
            
            // Check if band mode is active
            var bandManager = BandManager.Instance;
            if (bandManager != null && bandManager.IsBandSystemActive)
            {
                // Build a mapping from engine ID to player GUID
                var engineIdToPlayerId = BuildEngineIdToPlayerIdMap();
                int localBandId = bandManager.LocalPlayerBandId;
                
                // Only revive players in the same band as local player
                revivalFilter = (engineId) =>
                {
                    if (!engineIdToPlayerId.TryGetValue(engineId, out Guid playerId))
                    {
                        // Unknown player - don't revive (shouldn't happen)
                        return false;
                    }
                    
                    int playerBandId = bandManager.GetPlayerBandId(playerId);
                    return playerBandId == localBandId;
                };
                
                Debug.Log($"[GameManager] Star Power revival - band mode active, filtering to band {localBandId}");
            }
            
            // Revive failed players (with optional band filter)
            bool anyRevived = EngineManager.TryReviveFailedPlayers(revivalFilter);
            
            if (anyRevived)
            {
                // Clear the band-wide failure state if it was set
                if (PlayerHasFailed)
                {
                    PlayerHasFailed = false;
                    Debug.Log("[GameManager] Star Power revival - cleared PlayerHasFailed flag");
                }
                
                // Note: We no longer show a toast notification for star power revival
                // to avoid spam. A countdown/visual cue should be shown instead.
                
                // In multiplayer, broadcast the revival to other players
                var networkService = NetworkingServiceFactory.Instance;
                if (networkService != null && networkService.IsNetworkActive)
                {
                    // The revival state will be synced via the normal gameplay snapshot system
                    // The happiness values will automatically propagate
                    Debug.Log("[GameManager] Star Power revival - will sync via gameplay snapshots");
                }
            }
        }
        
        /// <summary>
        /// Builds a mapping from engine ID to network player GUID.
        /// Used for band-aware revival filtering.
        /// </summary>
        private Dictionary<int, Guid> BuildEngineIdToPlayerIdMap()
        {
            var map = new Dictionary<int, Guid>();
            
            if (_players == null)
                return map;
                
            foreach (var player in _players)
            {
                var engineContainer = player.PlayerEngineContainer;
                var networkData = player.NetworkPlayerData;
                
                if (engineContainer != null && networkData != null)
                {
                    // NetworkPlayerData.NetworkPlayerId is already a Guid
                    Guid playerId = networkData.NetworkPlayerId;
                    if (playerId != Guid.Empty)
                    {
                        map[engineContainer.EngineId] = playerId;
                    }
                }
            }
            
            return map;
        }

        public void ChangeStemMuteState(SongStem stem, bool muted, float duration = 0.0f)
        {
            var setting = SettingsManager.Settings.MuteOnMiss.Value;
            if (setting == AudioFxMode.Off
            || !_stemStates.TryGetValue(stem, out var state)
            || (setting == AudioFxMode.MultitrackOnly && stem == _backgroundStem))
            {
                return;
            }

            double volume = state.SetMute(muted);

            if (duration <= 0.0f)
            {
                GlobalAudioHandler.SetVolumeSetting(stem, volume);
                return;
            }

            if (_volumeTween == null || !_volumeTween.IsPlaying())
            {
                _volumeTween = DOTween.To(() => GlobalAudioHandler.GetVolumeSetting(stem),
                    x => GlobalAudioHandler.SetVolumeSetting(stem, x), volume, duration);
            }
            else
            {
                _volumeTween.ChangeEndValue(volume);
            }
        }

        public void ChangeStemReverbState(SongStem stem, bool reverb)
        {
            var setting = SettingsManager.Settings.UseStarpowerFx.Value;
            if (setting == AudioFxMode.Off)
            {
                return;
            }

            StemState state;
            while (!_stemStates.TryGetValue(stem, out state))
            {
                if (stem == _backgroundStem)
                {
                    return;
                }
                stem = _backgroundStem;
            }

            if (setting == AudioFxMode.MultitrackOnly && stem == _backgroundStem)
            {
                return;
            }

            bool reverbActive = state.SetReverb(reverb);
            GlobalAudioHandler.SetReverbSetting(stem, reverbActive);
        }

        public void ChangeStemWhammyPitch(SongStem stem, float percent)
        {
            // If Whammy FX is turned off, ignore.
            if (!SettingsManager.Settings.UseWhammyFx.Value)
            {
                return;
            }

            // If the specified stem is the same as the background stem,
            // ignore the request. This may be a chart without separate
            // stems for each instrument. In that scenario we don't want
            // to pitch bend because we'd be bending the entire track.
            if (stem == _backgroundStem)
            {
                return;
            }

            // If we can't get the state for the stem, bail.
            if (!_stemStates.TryGetValue(stem, out var state))
            {
                return;
            }

            // Set the pitch
            float percentActive = state.SetWhammyPitch(percent);
            GlobalAudioHandler.SetWhammyPitchSetting(stem, percentActive);
        }
    }
}