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
            Gun gun = player.CurrentGun;
            tk2dBaseSprite gunSprite = gun != null ? gun.sprite : null;
            // Hidden while dodge rolling, in cutscenes, as a ghost...: the game toggles the renderer.
            Renderer gunRenderer = gunSprite != null ? gunSprite.GetComponent<Renderer>() : null;

            return new PlayerPositionPacket
            {
                PlayerId = playerId,
                Position = new Vector2(pos3.x, pos3.y),
                Velocity = Vector2.zero,
                Rotation = player.transform.eulerAngles.z,
                IsGrounded = true,
                IsDodgeRolling = player.IsDodgeRolling,
                CharacterId = (int)player.characterIdentity,
                AltCostume = player.IsUsingAlternateCostume,
                // The frame actually on screen, so the remote avatar mirrors every animation as-is.
                SpriteId = player.sprite != null ? player.sprite.spriteId : -1,
                FlipX = player.sprite != null && player.sprite.FlipX,
                SpriteOffset = player.sprite != null ? (Vector2)(player.sprite.transform.position - pos3) : Vector2.zero,
                SendTime = Time.realtimeSinceStartup,
                GunId = gunSprite != null ? gun.PickupObjectId : -1,
                GunSpriteId = gunSprite != null ? gunSprite.spriteId : -1,
                GunOffset = gunSprite != null ? (Vector2)(gunSprite.transform.position - pos3) : Vector2.zero,
                GunAngle = gunSprite != null ? gunSprite.transform.eulerAngles.z : 0f,
                GunFlipY = gunSprite != null && gunSprite.FlipY,
                GunHeight = gunSprite != null ? gunSprite.HeightOffGround : 0f,
                GunVisible = gunRenderer != null && gunRenderer.enabled && !player.IsGhost,
                SceneHash = LocalSceneHash()
            };
        }

        private string _hashedScene;
        private int _sceneHash;

        /// <summary>
        /// FNV-1a of the current level's scene name, cached per scene. Hand-rolled rather than
        /// string.GetHashCode, which no runtime promises to keep stable between machines.
        /// </summary>
        private int LocalSceneHash()
        {
            string scene = WorldStateReplicator.CurrentSceneName();
            if (scene == _hashedScene) return _sceneHash;

            uint hash = 2166136261;
            foreach (char c in scene)
            {
                hash ^= c;
                hash *= 16777619;
            }
            _hashedScene = scene;
            _sceneHash = (int)hash;
            return _sceneHash;
        }

        public void SpawnRemotePlayer(ulong steamId, Vector2 position, float rotation)
        {
            if (TryGetLiveAvatar(steamId, out _)) return;

            RemotePlayerAvatar player = RemotePlayerAvatar.Create(steamId, position, rotation);
            // Something enemies can aim at for this player: the host's real enemies target it, and a
            // client's puppets point their replayed bullet scripts at it (see RemotePlayerTarget).
            player.EnableEnemyTarget(steamId);
            _remotePlayers[steamId] = player;
            Debug.LogInfo($"[PlayerReplicator] Spawned remote player {steamId} at {position}");
        }

        private static bool IsLocalLoading() => GameManager.HasInstance && GameManager.Instance.IsLoadingLevel;

        /// <summary>The stand-in target on a remote player's avatar, if it exists.</summary>
        public bool TryGetEnemyTarget(ulong steamId, out RemotePlayerTarget target)
        {
            target = TryGetLiveAvatar(steamId, out var avatar) ? avatar.EnemyTarget : null;
            return target != null;
        }

        /// <summary>Host: the stand-ins its enemies may target - one per living remote player on this level.</summary>
        public IEnumerable<RemotePlayerTarget> LivingEnemyTargets()
        {
            foreach (var kvp in _remotePlayers)
            {
                if (kvp.Value == null || kvp.Value.EnemyTarget == null) continue;
                if (!PlayerLifeReplicator.Instance.IsAlive(kvp.Key)) continue;
                yield return kvp.Value.EnemyTarget;
            }
        }

        public void UpdateRemotePlayer(PlayerPositionPacket packet)
        {
            if (IsLocalLoading()) return; // see Update: no avatars while loading
            ulong steamId = packet.PlayerId;

            // On another level (e.g. they went back to the Breach): no avatar here. Its position
            // would be meaningless, and it would keep co-op doors shut for good.
            if (packet.SceneHash != LocalSceneHash())
            {
                if (_remotePlayers.ContainsKey(steamId)) RemoveRemotePlayer(steamId);
                return;
            }

            if (!TryGetLiveAvatar(steamId, out var player))
            {
                SpawnRemotePlayer(steamId, packet.Position, packet.Rotation);
                if (!TryGetLiveAvatar(steamId, out player)) return;
            }

            player.Apply(packet);
            // Every packet, since a level load rebuilds the avatar; a no-op when unchanged.
            player.SetGhost(!PlayerLifeReplicator.Instance.IsAlive(steamId));
        }

        /// <summary>PlayerLifeReplicator: tint a ghost's avatar.</summary>
        public void SetRemoteGhost(ulong steamId, bool isGhost)
        {
            if (TryGetLiveAvatar(steamId, out var player)) player.SetGhost(isGhost);
        }

        // ---- Doors ----

        // How close a partner must be for a door to open. Vanilla co-op wants every player touching
        // it; a partner's avatar lags a little behind them, so allow a few units.
        private const float DoorPartnerRange = 3f;
        private const float DoorHintInterval = 3f;
        private float _nextDoorHintTime;

        /// <summary>
        /// DungeonDoorController.CheckForPlayerCollision prefix. Like vanilla co-op, the local player
        /// can open a door only while every living partner is next to them, so nobody walks into a
        /// fight alone - or gets warped into one. False keeps the door shut.
        /// </summary>
        public static bool CanOpenDoor(DungeonDoorController door, SpeculativeRigidbody toucher)
        {
            try
            {
                return Instance.CheckDoor(door, toucher);
            }
            catch (System.Exception e)
            {
                Debug.LogWarningThrottled("PlayerReplicator.DoorCheck", $"[PlayerReplicator] Door check failed, letting it open: {e.GetType().Name}: {e.Message}");
                return true;
            }
        }

        private bool CheckDoor(DungeonDoorController door, SpeculativeRigidbody toucher)
        {
            if (!NetworkSession.Instance.IsConnected || door == null || toucher == null) return true;
            // The original ignores touches on these; don't show the hint for them either.
            if (door.IsOpen || door.IsSealed || door.isLocked) return true;

            GameManager gm = GameManager.Instance;
            if (gm == null || gm.IsFoyer || gm.IsLoadingLevel) return true;
            PlayerController local = gm.PrimaryPlayer;
            if (local == null || toucher.gameObject != local.gameObject) return true;

            Vector2 localPos = local.transform.position;
            // Ghosts too: whoever is spectating comes along, so the fight (and a ghost host, whose
            // room entry wakes the enemies) never splits up.
            if (AllPartners(pos => Vector2.Distance(pos, localPos) <= DoorPartnerRange, includeGhosts: true)) return true;

            ShowWaitingHint(local);
            return false;
        }

        /// <summary>
        /// Whether every remote player's latest position (transform origin, like
        /// PlayerController.transform.position) passes the test; ghosts count only with includeGhosts.
        /// A partner with no avatar - still loading in, or on another level - doesn't block: better
        /// than a door or exit that never opens because an avatar went missing.
        /// </summary>
        public bool AllPartners(System.Predicate<Vector2> test, bool includeGhosts)
        {
            foreach (var kvp in _remotePlayers)
            {
                if (kvp.Value == null || (!includeGhosts && !PlayerLifeReplicator.Instance.IsAlive(kvp.Key))) continue;
                if (!test(kvp.Value.NetworkPosition)) return false;
            }
            return true;
        }

        /// <summary>"Waiting for my partner..." over the local player, at most every few seconds.</summary>
        public void ShowWaitingHint(PlayerController local)
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextDoorHintTime || local.sprite == null) return;
            _nextDoorHintTime = now + DoorHintInterval;
            TextBoxManager.ShowThoughtBubble(local.sprite.WorldTopCenter + new Vector2(0f, 0.5f), local.transform, 1.5f, "Waiting for my partner...");
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
