using System.Collections.Generic;
using UnityEngine;
using GungeonTogether.Networking.Players;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Networking.Transport;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Single owner of player state sync, both directions: broadcasts/sends the local player's own
    /// position and stats, and applies incoming position/stat packets to the right remote
    /// RemotePlayerAvatar. Replaces the old PlayerManager + PlayerSyncManager split - both were
    /// "player" concerns (position vs. stats) living in separate managers with separate timers,
    /// which is how a stat packet ended up being applied to the local player instead of a remote
    /// one: there was nowhere on the remote side to put it.
    /// </summary>
    public class PlayerReplicator : MonoSingleton<PlayerReplicator>
    {
        private const float PositionSendInterval = 1f / 30f; // 30 Hz; receivers interpolate on the sender's timestamps, so uneven frame timing doesn't show
        private const float StatsSendInterval = 0.5f;

        private readonly Dictionary<ulong, RemotePlayerAvatar> _remotePlayers = new Dictionary<ulong, RemotePlayerAvatar>();

        private float _nextPositionSendTime;
        private float _nextStatsSendTime;

        private const float StatusLogInterval = 15f;
        private float _nextStatusLogTime;
        // Frame timing for the status line - tells "our game stutters" apart from "the avatar stutters".
        private int _framesSinceStatus;
        private float _worstFrameSinceStatus;

        private void Update()
        {
            if (!NetworkSession.Instance.IsConnected) return;

            // An avatar created while our level loads survives into the new level but is never drawn
            // (seen when both players enter a floor together). So hold no avatars during a load -
            // the first position packet afterwards builds a fresh one - and don't send our own
            // mid-transition position either.
            if (IsLocalLoading())
            {
                if (_remotePlayers.Count > 0) ClearAll();
                return;
            }

            float now = Time.realtimeSinceStartup;
            _framesSinceStatus++;
            _worstFrameSinceStatus = Mathf.Max(_worstFrameSinceStatus, Time.unscaledDeltaTime);

            if (now >= _nextPositionSendTime)
            {
                _nextPositionSendTime = now + PositionSendInterval;
                BroadcastLocalPosition();
            }

            // Both roles publish their own stats: the host broadcasts, a client sends to the host,
            // which applies them to that client's avatar and relays them to everyone else.
            if (now >= _nextStatsSendTime)
            {
                _nextStatsSendTime = now + StatsSendInterval;
                BroadcastLocalStats();
            }

            if (now >= _nextStatusLogTime)
            {
                _nextStatusLogTime = now + StatusLogInterval;
                LogAvatarStatus();
            }
        }

        /// <summary>
        /// Periodic "where is everyone" line - remote avatars have been invisible after level loads,
        /// and this tells a lost/unmoving avatar apart from one that's alive but off-screen.
        /// </summary>
        private void LogAvatarStatus()
        {
            PlayerController local = LocalPlayer();
            string localPos = local != null ? local.transform.position.ToString() : "(none)";
            Debug.LogInfo($"[PlayerReplicator] Frames: avg {_framesSinceStatus / StatusLogInterval:0} fps, worst frame {_worstFrameSinceStatus * 1000f:0} ms");
            _framesSinceStatus = 0;
            _worstFrameSinceStatus = 0f;
            foreach (var kvp in _remotePlayers)
            {
                RemotePlayerAvatar avatar = kvp.Value;
                string state = avatar == null
                    ? "destroyed"
                    : $"pos={avatar.transform.position}, sprite={(avatar.HasSprite ? "yes" : "NO")}, lastUpdate={Time.realtimeSinceStartup - avatar.LastUpdateTime:0.0}s ago";
                Debug.LogInfo($"[PlayerReplicator] Avatar {kvp.Key}: {state} | local={localPos} scene={WorldStateReplicator.CurrentSceneName()}");
            }
            if (_remotePlayers.Count == 0)
            {
                Debug.LogInfo($"[PlayerReplicator] No remote avatars | local={localPos} scene={WorldStateReplicator.CurrentSceneName()}");
            }
        }

        private void BroadcastLocalPosition()
        {
            ulong localId = SteamworksLocalId();
            if (localId == 0) return;

            var packet = CreateLocalPositionPacket(localId);
            if (packet == null) return;

            if (NetworkSession.Instance.IsHost)
            {
                NetworkSession.Instance.Broadcast(packet, reliable: false);
            }
            else if (NetworkSession.Instance.IsClient)
            {
                NetworkSession.Instance.SendToHost(packet, reliable: false);
            }
        }

        private void BroadcastLocalStats()
        {
            PlayerController player = LocalPlayer();
            if (player == null || player.healthHaver == null) return;

            Gun currentGun = player.inventory != null ? player.inventory.CurrentGun : null;
            int gunIndex = currentGun != null && player.inventory.AllGuns != null
                ? player.inventory.AllGuns.IndexOf(currentGun)
                : -1;

            var packet = new PlayerStatePacket
            {
                PlayerId = SteamworksLocalId(),
                Health = player.healthHaver.GetCurrentHealth(),
                MaxHealth = player.healthHaver.GetMaxHealth(),
                Armor = player.healthHaver.Armor,
                // HealthHaver doesn't expose a separate "max armor" - Armor is the only value ETG tracks.
                MaxArmor = player.healthHaver.Armor,
                Ammo = currentGun != null ? currentGun.CurrentAmmo : 0,
                MaxAmmo = currentGun != null ? currentGun.AdjustedMaxAmmo : 0,
                CurrentGunIndex = gunIndex,
                ActiveItemName = player.CurrentItem != null ? player.CurrentItem.name : ""
            };

            if (NetworkSession.Instance.IsHost)
            {
                NetworkSession.Instance.Broadcast(packet, reliable: false);
            }
            else if (NetworkSession.Instance.IsClient)
            {
                NetworkSession.Instance.SendToHost(packet, reliable: false);
            }
        }

        private static ulong SteamworksLocalId() => SteamIdentity.GetLocalSteamId();

        private static PlayerController LocalPlayer()
        {
            return GameManager.Instance != null ? GameManager.Instance.PrimaryPlayer : null;
        }

        public PlayerPositionPacket CreateLocalPositionPacket(ulong playerId)
        {
            PlayerController player = LocalPlayer();
            if (player == null) return null;

            Vector3 pos3 = player.transform.position;
            return new PlayerPositionPacket
            {
                PlayerId = playerId,
                Position = new Vector2(pos3.x, pos3.y),
                Velocity = Vector2.zero,
                Rotation = player.transform.eulerAngles.z,
                IsGrounded = true,
                IsDodgeRolling = player.IsDodgeRolling,
                CharacterId = (int)player.characterIdentity,
                // The frame actually on screen, so the remote avatar mirrors every animation as-is.
                SpriteId = player.sprite != null ? player.sprite.spriteId : -1,
                FlipX = player.sprite != null && player.sprite.FlipX,
                SendTime = Time.realtimeSinceStartup
            };
        }

        public void SpawnRemotePlayer(ulong steamId, Vector2 position, float rotation)
        {
            if (TryGetLiveAvatar(steamId, out _)) return;

            RemotePlayerAvatar player = RemotePlayerAvatar.Create(steamId, position, rotation);
            _remotePlayers[steamId] = player;
            Debug.LogInfo($"[PlayerReplicator] Spawned remote player {steamId} at {position}");
        }

        private static bool IsLocalLoading() => GameManager.HasInstance && GameManager.Instance.IsLoadingLevel;

        public void UpdateRemotePlayer(PlayerPositionPacket packet)
        {
            if (IsLocalLoading()) return; // see Update: no avatars while loading
            ulong steamId = packet.PlayerId;
            if (!TryGetLiveAvatar(steamId, out var player))
            {
                SpawnRemotePlayer(steamId, packet.Position, packet.Rotation);
                if (!TryGetLiveAvatar(steamId, out player)) return;
            }

            player.Apply(packet.Position, packet.Rotation, packet.FlipX, packet.CharacterId, packet.SpriteId, packet.SendTime);
        }

        /// <summary>Latest known position of a remote player, if their avatar exists.</summary>
        public bool TryGetRemotePosition(ulong steamId, out Vector2 position)
        {
            position = Vector2.zero;
            if (!TryGetLiveAvatar(steamId, out var player)) return false;
            position = player.NetworkPosition;
            return true;
        }

        /// <summary>
        /// Avatars are ordinary scene objects, so every level load destroys them while this
        /// dictionary still holds the dead reference - and C#'s ?./ContainsKey can't tell. Drop such
        /// entries so the next position packet respawns the avatar in the new level.
        /// </summary>
        private bool TryGetLiveAvatar(ulong steamId, out RemotePlayerAvatar player)
        {
            if (!_remotePlayers.TryGetValue(steamId, out player)) return false;
            if (player != null) return true; // Unity's overloaded ==: false once destroyed

            _remotePlayers.Remove(steamId);
            player = null;
            Debug.LogInfo($"[PlayerReplicator] Avatar for {steamId} was destroyed (level load); respawning on next update.");
            return false;
        }

        /// <summary>Applies incoming stats to the remote player they describe - never to the local player.</summary>
        public void ApplyPlayerState(PlayerStatePacket packet)
        {
            if (packet.PlayerId == SteamworksLocalId()) return;

            if (!TryGetLiveAvatar(packet.PlayerId, out var player))
            {
                Debug.LogWarningThrottled($"PlayerReplicator.UnknownState:{packet.PlayerId}", $"[PlayerReplicator] Got state for unknown player {packet.PlayerId} (not spawned yet).");
                return;
            }

            player.ApplyState(packet);
        }

        public void RemoveRemotePlayer(ulong steamId)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                if (player != null) Destroy(player.gameObject);
                _remotePlayers.Remove(steamId);
                Debug.LogInfo($"[PlayerReplicator] Removed remote player {steamId}");
            }
        }

        public void ClearAll()
        {
            foreach (var kvp in _remotePlayers)
                if (kvp.Value != null) Destroy(kvp.Value.gameObject);
            _remotePlayers.Clear();
        }
    }
}
