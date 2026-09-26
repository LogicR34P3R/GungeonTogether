using System.Collections.Generic;
using UnityEngine;
using GungeonTogether.Networking;
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
        private const float PositionSendInterval = 0.25f;
        private const float StatsSendInterval = 0.5f;

        private readonly Dictionary<ulong, RemotePlayerAvatar> _remotePlayers = new Dictionary<ulong, RemotePlayerAvatar>();

        private float _nextPositionSendTime;
        private float _nextStatsSendTime;

        private void Update()
        {
            if (!NetworkSession.Instance.IsConnected) return;

            float now = Time.realtimeSinceStartup;

            if (now >= _nextPositionSendTime)
            {
                _nextPositionSendTime = now + PositionSendInterval;
                BroadcastLocalPosition();
            }

            // Only the host publishes authoritative stats today - matches the pre-Replication
            // behavior; clients don't yet send their own stats anywhere.
            if (NetworkSession.Instance.IsHost && now >= _nextStatsSendTime)
            {
                _nextStatsSendTime = now + StatsSendInterval;
                BroadcastLocalStats();
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

            NetworkSession.Instance.Broadcast(packet, reliable: false);
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
                AnimationState = player.spriteAnimator != null ? player.spriteAnimator.CurrentFrame : 0,
                SpriteId = -1,
                FlipX = player.sprite != null && player.sprite.FlipX
            };
        }

        public void SpawnRemotePlayer(ulong steamId, Vector2 position, float rotation)
        {
            if (_remotePlayers.ContainsKey(steamId)) return;

            RemotePlayerAvatar player = RemotePlayerAvatar.Create(steamId, position, rotation);
            _remotePlayers[steamId] = player;
            Debug.LogInfo($"[PlayerReplicator] Spawned remote player {steamId} at {position}");
        }

        public void UpdateRemotePlayer(ulong steamId, Vector2 position, float rotation, bool flipX = false)
        {
            if (!_remotePlayers.TryGetValue(steamId, out var player))
            {
                SpawnRemotePlayer(steamId, position, rotation);
                _remotePlayers.TryGetValue(steamId, out player);
            }

            player?.Apply(position, rotation, flipX);
        }

        /// <summary>Applies incoming stats to the remote player they describe - never to the local player.</summary>
        public void ApplyPlayerState(PlayerStatePacket packet)
        {
            if (packet.PlayerId == SteamworksLocalId()) return;

            if (!_remotePlayers.TryGetValue(packet.PlayerId, out var player))
            {
                Debug.LogWarning($"[PlayerReplicator] Got state for unknown player {packet.PlayerId} (not spawned yet).");
                return;
            }

            player.ApplyState(packet);
        }

        public void RemoveRemotePlayer(ulong steamId)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                Destroy(player.gameObject);
                _remotePlayers.Remove(steamId);
                Debug.LogInfo($"[PlayerReplicator] Removed remote player {steamId}");
            }
        }

        public void ClearAll()
        {
            foreach (var kvp in _remotePlayers)
                Destroy(kvp.Value.gameObject);
            _remotePlayers.Clear();
        }
    }
}
