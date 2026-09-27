using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Shared money/keys pool (step 3b), like vanilla co-op - where the second player simply uses
    /// the primary player's PlayerConsumables (PlayerController.cs:1374).
    ///
    /// The host's PlayerConsumables is the pool. Each side polls its own pool for changes against
    /// the last synced values: the host broadcasts absolute values (ConsumablesState); a client sends
    /// its change as a delta (ConsumablesDelta), which the host adds to the pool and re-broadcasts.
    /// Deltas rather than absolutes so two players spending/collecting at once both count.
    ///
    /// Silent re-baselines (no delta sent) on level loads, a new PlayerController, and in the foyer:
    /// a new run resets each side's pool on its own, and sending that reset as a delta would
    /// subtract it from the host's pool a second time. After re-baselining, the host re-broadcasts
    /// and the client adopts the host's latest values, so the two converge whichever loads first.
    /// </summary>
    public class ConsumablesReplicator : MonoSingleton<ConsumablesReplicator>
    {
        private struct Values
        {
            public int Currency, KeyBullets, RatKeys;
            public bool IsZero => Currency == 0 && KeyBullets == 0 && RatKeys == 0;
        }

        private PlayerConsumables _pool;
        private Values _lastSynced;
        private bool _needsRebaseline = true;
        private bool _wasLoading;

        // Client: the host's latest pool, applied when we're able to (e.g. once our own load ends).
        private ConsumablesStatePacket _hostState;
        // Host: client changes that arrived while we couldn't apply them (mid-load).
        private Values _pendingClientDelta;

        private static PlayerConsumables CurrentPool()
        {
            GameManager gm = GameManager.Instance;
            PlayerController player = gm != null ? gm.PrimaryPlayer : null;
            return player != null ? player.carriedConsumables : null;
        }

        private static Values Read(PlayerConsumables pool) => new Values
        {
            Currency = pool.Currency,
            KeyBullets = pool.KeyBullets,
            RatKeys = pool.ResourcefulRatKeys
        };

        private void Update()
        {
            GameManager gm = GameManager.Instance;
            bool loading = gm == null || gm.IsLoadingLevel;
            if (_wasLoading && !loading) _needsRebaseline = true;
            _wasLoading = loading;

            PlayerConsumables pool = CurrentPool();
            if (!NetworkSession.Instance.IsConnected || loading || pool == null) return;

            if (pool != _pool || _needsRebaseline)
            {
                _pool = pool;
                _needsRebaseline = false;
                Rebaseline();
                return;
            }

            if (NetworkSession.Instance.IsHost && !_pendingClientDelta.IsZero && !gm.IsFoyer)
            {
                Values pending = _pendingClientDelta;
                _pendingClientDelta = default;
                ApplyDeltaAndBroadcast(pending);
                return;
            }

            FlushLocalChanges();
        }

        private void Rebaseline()
        {
            if (NetworkSession.Instance.IsHost)
            {
                _lastSynced = Read(_pool);
                BroadcastState();
            }
            else
            {
                if (_hostState != null) ApplyHostState(_hostState);
                _lastSynced = Read(_pool);
            }
        }

        /// <summary>Report whatever changed locally since the last sync.</summary>
        private void FlushLocalChanges()
        {
            Values current = Read(_pool);

            // Run resets and character swaps happen in the foyer - never sync them as gains/losses.
            if (GameManager.Instance.IsFoyer)
            {
                _lastSynced = current;
                return;
            }

            var delta = new Values
            {
                Currency = current.Currency - _lastSynced.Currency,
                KeyBullets = current.KeyBullets - _lastSynced.KeyBullets,
                RatKeys = current.RatKeys - _lastSynced.RatKeys
            };
            if (delta.IsZero) return;
            _lastSynced = current;

            if (NetworkSession.Instance.IsHost)
            {
                BroadcastState(); // the host's pool already includes its own change
            }
            else
            {
                NetworkSession.Instance.SendToHost(new ConsumablesDeltaPacket
                {
                    Currency = delta.Currency,
                    KeyBullets = delta.KeyBullets,
                    RatKeys = delta.RatKeys
                }, reliable: true);
            }
        }

        private void BroadcastState()
        {
            Values v = Read(_pool);
            NetworkSession.Instance.Broadcast(new ConsumablesStatePacket
            {
                Currency = v.Currency,
                KeyBullets = v.KeyBullets,
                RatKeys = v.RatKeys
            }, reliable: true);
        }

        /// <summary>Host: a joining client adopts the current pool.</summary>
        public void SendCurrentStateTo(ulong targetId)
        {
            PlayerConsumables pool = CurrentPool();
            if (pool == null) return;
            Values v = Read(pool);
            NetworkSession.Instance.SendPacket(targetId, new ConsumablesStatePacket
            {
                Currency = v.Currency,
                KeyBullets = v.KeyBullets,
                RatKeys = v.RatKeys
            }, reliable: true);
        }

        // ---- Host: client changes ----

        public void HandleDelta(ConsumablesDeltaPacket packet)
        {
            var delta = new Values { Currency = packet.Currency, KeyBullets = packet.KeyBullets, RatKeys = packet.RatKeys };

            if (!IsReady())
            {
                _pendingClientDelta.Currency += delta.Currency;
                _pendingClientDelta.KeyBullets += delta.KeyBullets;
                _pendingClientDelta.RatKeys += delta.RatKeys;
                return;
            }
            ApplyDeltaAndBroadcast(delta);
        }

        private void ApplyDeltaAndBroadcast(Values delta)
        {
            // Count our own not-yet-reported change first, so the re-baseline below doesn't swallow it.
            FlushLocalChanges();

            _pool.Currency += delta.Currency; // the setter clamps at 0
            _pool.KeyBullets = System.Math.Max(0, _pool.KeyBullets + delta.KeyBullets);
            _pool.ResourcefulRatKeys = System.Math.Max(0, _pool.ResourcefulRatKeys + delta.RatKeys);
            _lastSynced = Read(_pool);
            BroadcastState();

            Debug.Log($"[ConsumablesReplicator] Client change {delta.Currency:+0;-0;0} money, {delta.KeyBullets:+0;-0;0} keys -> pool {_lastSynced.Currency}/{_lastSynced.KeyBullets}.");
        }

        // ---- Client: host's pool ----

        public void HandleState(ConsumablesStatePacket packet)
        {
            _hostState = packet;
            if (!IsReady()) return; // Rebaseline applies it once we are

            // Send anything we changed but haven't reported yet before overwriting it.
            FlushLocalChanges();
            ApplyHostState(packet);
            _lastSynced = Read(_pool);
        }

        private void ApplyHostState(ConsumablesStatePacket state)
        {
            // Only assign what differs - the setters also bump stats and refresh the UI.
            if (_pool.Currency != state.Currency) _pool.Currency = state.Currency;
            if (_pool.KeyBullets != state.KeyBullets) _pool.KeyBullets = state.KeyBullets;
            if (_pool.ResourcefulRatKeys != state.RatKeys) _pool.ResourcefulRatKeys = state.RatKeys;
        }

        private bool IsReady()
        {
            GameManager gm = GameManager.Instance;
            return gm != null && !gm.IsLoadingLevel && _pool != null && _pool == CurrentPool() && !_needsRebaseline;
        }

        /// <summary>Called from NetworkSession.Shutdown. Each player keeps the pool as it stood.</summary>
        public void ResetSessionState()
        {
            _pool = null;
            _needsRebaseline = true;
            _hostState = null;
            _pendingClientDelta = default;
        }
    }
}
