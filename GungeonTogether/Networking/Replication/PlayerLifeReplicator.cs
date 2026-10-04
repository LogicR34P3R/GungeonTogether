using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Dungeonator;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Networking.Transport;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Vanilla co-op's death rules across the network.
    ///
    /// Dying while a partner is still alive makes you a ghost instead of ending the run: you keep
    /// moving (spectating) and can use the ghost blank, and the partner plays on. The game's own
    /// PlayerController.Die only does this for a local second player, so a Die prefix
    /// (PlayerPatches) diverts it here. Unlike vanilla, nothing is dropped and the loadout isn't
    /// reset: the partner couldn't pick the items up on their side anyway.
    ///
    /// A ghost is revived when the floor is cleared (the boss dies - vanilla's rule too) or at the
    /// next level load, as a safety net. The run ends for everyone only once nobody is alive: the
    /// last player to die gets the normal game over, and a ghost starts its own on hearing that.
    /// </summary>
    public class PlayerLifeReplicator : MonoSingleton<PlayerLifeReplicator>
    {
        // Private in PlayerController; the co-op death path is built from them.
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly MethodInfo BecomeGhostMethod = typeof(PlayerController).GetMethod("BecomeGhost", Private);
        private static readonly MethodInfo HandleDeathMethod = typeof(PlayerController).GetMethod("HandleDeath_CR", Private);
        private static readonly MethodInfo CoopResurrectMethod = typeof(PlayerController).GetMethod("CoopResurrectInternal", Private);
        private static readonly FieldInfo HandlingQueuedAnimationField = typeof(PlayerController).GetField("m_handlingQueuedAnimation", Private);

        private readonly Dictionary<ulong, PlayerLifeState> _remoteStates = new Dictionary<ulong, PlayerLifeState>();
        private bool _gameOverStarted;
        private bool _reviving;
        private GameManager _subscribedTo;

        public bool IsAlive(ulong playerId) =>
            !_remoteStates.TryGetValue(playerId, out PlayerLifeState state) || state == PlayerLifeState.Alive;

        /// <summary>
        /// HealthHaver.BossHealthSanityCheck postfix. The game refuses the killing blow on a boss
        /// while the primary player is dead and no local co-op partner lives, so a dead host
        /// (ghost) made every boss unkillable for its clients: their hits arrive as host damage.
        /// A living partner counts as that second player.
        /// </summary>
        public static bool AllowBossDamage(bool allowed) =>
            allowed || (NetworkSession.Instance.IsHost && Instance.AnyPartnerAlive());

        private void Update()
        {
            GameManager gm = GameManager.HasInstance ? GameManager.Instance : null;
            if (gm == null) return;
            if (_subscribedTo != gm)
            {
                if (_subscribedTo != null) _subscribedTo.OnNewLevelFullyLoaded -= OnLevelLoaded;
                gm.OnNewLevelFullyLoaded += OnLevelLoaded;
                _subscribedTo = gm;
            }

            // A new run (the foyer) starts everyone alive again.
            if (gm.IsFoyer)
            {
                _gameOverStarted = false;
                if (_remoteStates.Count > 0) _remoteStates.Clear();
            }
        }

        // ---- Local death ----

        /// <summary>
        /// PlayerController.Die prefix. True runs the game's own death (game over); false means the
        /// player became a ghost instead.
        /// </summary>
        public static bool OnLocalDying(PlayerController player)
        {
            try
            {
                return Instance.HandleLocalDying(player);
            }
            catch (Exception e)
            {
                Debug.LogError($"[PlayerLife] Ghost death failed, using the normal death: {e.GetType().Name}: {e.Message}");
                return true;
            }
        }

        private bool HandleLocalDying(PlayerController player)
        {
            GameManager gm = GameManager.Instance;
            if (!NetworkSession.Instance.IsConnected || gm == null || player != gm.PrimaryPlayer) return true;

            if (gm.IsFoyer || !AnyPartnerAlive() || BecomeGhostMethod == null)
            {
                // The last one standing: the run ends here. Tell the others - a ghost among them
                // starts its own game over.
                _gameOverStarted = true;
                SendLocalState(PlayerLifeState.Dead);
                Debug.LogInfo("[PlayerLife] Died with nobody left alive; game over.");
                return true;
            }

            player.StartCoroutine(BecomeGhost(player));
            SendLocalState(PlayerLifeState.Ghost);
            Debug.LogInfo("[PlayerLife] Died while a partner is alive; spectating as a ghost until the floor is cleared.");
            return false;
        }

        /// <summary>PlayerController.HandleCoopDeath without the item drop, loadout reset and co-op chests.</summary>
        private static IEnumerator BecomeGhost(PlayerController player)
        {
            player.CurrentInputState = PlayerInputState.NoInput;
            // Stops the normal animation update from overriding the death animation.
            if (HandlingQueuedAnimationField != null) HandlingQueuedAnimationField.SetValue(player, true);
            if (player.CurrentGun != null) player.CurrentGun.CeaseAttack(false);
            player.specRigidbody.Velocity = Vector2.zero;
            player.specRigidbody.enabled = false;
            player.IsOnFire = false;
            player.ToggleHandRenderers(false, string.Empty);
            player.ToggleGunRenderers(false, string.Empty);
            if (GameUIRoot.HasInstance) GameUIRoot.Instance.ForceClearReload(player.PlayerIDX);

            string deathAnim = player.UseArmorlessAnim ? "death_coop_armorless" : "death_coop";
            if (!player.IsFalling && player.spriteAnimator != null && player.spriteAnimator.GetClipByName(deathAnim) != null)
            {
                player.spriteAnimator.Play(deathAnim);
                while (player != null && player.spriteAnimator.IsPlaying(deathAnim)) yield return null;
            }
            if (player == null) yield break;

            // Ghost look, collision with walls only, input back on.
            BecomeGhostMethod.Invoke(player, null);
        }

        private bool AnyPartnerAlive()
        {
            foreach (ulong peerId in NetworkSession.Instance.ConnectedPeerIds)
            {
                if (IsAlive(peerId)) return true;
            }
            return false;
        }

        // ---- Revive ----

        /// <summary>The floor boss died (host: Dungeon.FloorCleared postfix; client: the host's FloorCleared).</summary>
        public void OnFloorCleared() => TryReviveLocal("the floor was cleared");

        private void OnLevelLoaded()
        {
            // After a game over the next level is a new run (quick restart skips the foyer).
            _gameOverStarted = false;
            TryReviveLocal("a new level loaded");
        }

        private void TryReviveLocal(string reason)
        {
            PlayerController player = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (player == null || !player.IsGhost || _reviving || _gameOverStarted || CoopResurrectMethod == null) return;

            player.StartCoroutine(Revive(player));
            Debug.LogInfo($"[PlayerLife] Reviving: {reason}.");
        }

        /// <summary>
        /// The game's own co-op revive (CoopResurrectInternal). Its very last step recalculates the
        /// co-op partner's stats, and there is no local partner, so it throws right before it gives
        /// input back - that step is run here instead.
        /// </summary>
        private IEnumerator Revive(PlayerController player)
        {
            _reviving = true;
            IEnumerator vanilla;
            try
            {
                vanilla = (IEnumerator)CoopResurrectMethod.Invoke(player, new object[] { ReviveSpot(player), ReviveClip(player), false });
            }
            catch (Exception e)
            {
                _reviving = false;
                Debug.LogError($"[PlayerLife] Revive failed to start: {e.GetType().Name}: {e.Message}");
                yield break;
            }

            while (true)
            {
                bool more;
                try
                {
                    more = vanilla.MoveNext();
                }
                catch (NullReferenceException)
                {
                    break; // the missing partner (see summary)
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PlayerLife] Revive: {e.GetType().Name}: {e.Message}");
                    break;
                }
                if (!more) break;
                yield return vanilla.Current;
            }

            _reviving = false;
            if (player == null) yield break;
            player.CurrentInputState = PlayerInputState.AllInput;
            player.healthHaver.IsVulnerable = true;
            player.stats.RecalculateStats(player);
            HideSecondPlayerAmmoPanel(player);
            SendLocalState(PlayerLifeState.Alive);
        }

        /// <summary>Where ResurrectFromBossKill would put the player: here, or the room's reward spot if here isn't floor.</summary>
        private static Vector3 ReviveSpot(PlayerController player)
        {
            Vector3 here = player.transform.position;
            DungeonData data = GameManager.Instance.Dungeon != null ? GameManager.Instance.Dungeon.data : null;
            CellData cell = data != null ? data[here.IntXY(VectorConversions.Floor)] : null;
            if (cell != null && cell.type == CellType.FLOOR && !cell.IsPlayerInaccessible) return here;

            RoomHandler room = player.CurrentRoom;
            return room != null ? room.GetBestRewardLocation(IntVector2.One, RoomHandler.RewardLocationStyle.PlayerCenter).ToVector3() : here;
        }

        private static tk2dSpriteAnimationClip ReviveClip(PlayerController player)
        {
            if (player.spriteAnimator == null) return null;
            string armorless = player.UseArmorlessAnim ? "_armorless" : "";
            return player.spriteAnimator.GetClipByName("chest_recover") != null
                ? player.spriteAnimator.GetClipByName("chest_recover" + armorless)
                : player.spriteAnimator.GetClipByName("pitfall_return" + armorless);
        }

        /// <summary>The co-op revive re-shows the second player's ammo panel, which single-player never uses.</summary>
        private static void HideSecondPlayerAmmoPanel(PlayerController player)
        {
            GameUIRoot ui = GameUIRoot.HasInstance ? GameUIRoot.Instance : null;
            if (ui == null || ui.ammoControllers == null) return;
            int other = player.IsPrimaryPlayer ? 1 : 0;
            if (other >= ui.ammoControllers.Count || ui.ammoControllers[other] == null) return;

            ui.ammoControllers[other].ToggleRenderers(false);
            dfPanel panel = ui.ammoControllers[other].GetComponent<dfPanel>();
            if (panel != null) panel.IsVisible = false;
        }

        // ---- Remote players ----

        public void HandleRemoteLife(PlayerLifePacket packet)
        {
            _remoteStates[packet.PlayerId] = packet.State;
            PlayerReplicator.Instance.SetRemoteGhost(packet.PlayerId, packet.State != PlayerLifeState.Alive);
            Debug.LogInfo($"[PlayerLife] Player {packet.PlayerId} is now {packet.State}.");

            // Everyone else is down too: nobody is left to clear the floor, so end the run.
            PlayerController player = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (player != null && player.IsGhost && !AnyPartnerAlive()) StartGameOver(player);
        }

        /// <summary>The second half of PlayerController.Die, for a player who is already a ghost.</summary>
        private void StartGameOver(PlayerController player)
        {
            if (_gameOverStarted || HandleDeathMethod == null) return;
            _gameOverStarted = true;
            SendLocalState(PlayerLifeState.Dead);
            Debug.LogInfo("[PlayerLife] Everyone is down; game over.");

            GameManager gm = GameManager.Instance;
            gm.PauseRaw(preventUnpausing: true);
            BraveTime.RegisterTimeScaleMultiplier(0f, gm.gameObject);
            AkSoundEngine.PostEvent("Stop_SND_All", player.gameObject);
            player.StartCoroutine((IEnumerator)HandleDeathMethod.Invoke(player, null));
            AkSoundEngine.PostEvent("Play_UI_gameover_start_01", player.gameObject);
        }

        private static void SendLocalState(PlayerLifeState state)
        {
            var packet = new PlayerLifePacket { PlayerId = SteamIdentity.GetLocalSteamId(), State = state };
            if (NetworkSession.Instance.IsHost) NetworkSession.Instance.Broadcast(packet, reliable: true);
            else if (NetworkSession.Instance.IsClient) NetworkSession.Instance.SendToHost(packet, reliable: true);
        }

        /// <summary>Called from NetworkSession.Shutdown. A ghost left without partners gets back up.</summary>
        public void ResetSessionState()
        {
            _remoteStates.Clear();
            TryReviveLocal("the session ended");
        }
    }
}
