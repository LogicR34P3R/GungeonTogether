using System.Collections.Generic;
using UnityEngine;
using Brave.BulletScript;
using GungeonTogether.Networking.Entities;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;
using Object = UnityEngine.Object;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Enemy attack script replay (step 4c-2, option B; first bosses only, now every synced enemy).
    /// Instead of 4a's straight bullets, the client runs the enemy's actual BulletScript from its
    /// puppet, so spirals, curves, homing, splits and bullets that wait before flying look right.
    /// Scripts aim with BulletManager.PlayerPosition() - the puppet's target, which EnemyReplicator
    /// sets to whoever the host's enemy is targeting (EnemyState.TargetId).
    ///
    /// Determinism: scripts draw from UnityEngine.Random. Each script bullet here gets its own random
    /// stream, swapped into UnityEngine.Random around that bullet's Initialize/FrameUpdate (a stack,
    /// since a parent's tick initialises the children it fires). Streams are per *bullet*, not per
    /// script: Unity updates bullets in an order that isn't guaranteed to match across machines, but a
    /// child's seed only depends on its parent's seed and its firing order, which do match. The host
    /// runs its own replayed scripts through the same streams - otherwise there'd be nothing to match.
    /// Script ticks are frame-count based (Bullet.FrameUpdate steps in 1/60s), so timing matches too,
    /// shifted by network delay.
    ///
    /// 4a's straight bullets still cover shots that aren't scripts (held guns, direct bank shots), and
    /// bullets from replayed scripts are excluded from 4a. Switchable off via the host's [Sync]
    /// BossScriptReplay config (name kept for existing configs), in which case everything falls back to 4a.
    ///
    /// Harmony wiring: GungeonTogether.Patches.ScriptPatches.
    /// </summary>
    public class ScriptReplicator : MonoSingleton<ScriptReplicator>
    {
        private const float SweepInterval = 1f;

        /// <summary>Host-side switch, bound to config in GungeonTogetherMod.</summary>
        public static bool Enabled { get; set; } = true;

        private class RandomStream
        {
            public int Seed;
            public Random.State State;
            public int FireCount;
        }

        private struct Frame
        {
            public Bullet Bullet;
            public RandomStream Stream; // null: this bullet isn't replayed - nothing swapped
            public Random.State Saved;
        }

        private static readonly Dictionary<Bullet, RandomStream> _streams = new Dictionary<Bullet, RandomStream>();
        // Used as a stack; a List so Suspend/Resume can update a frame in place.
        private static readonly List<Frame> _frames = new List<Frame>();
        // Indices into _frames of streams suspended around a child spawn (-1: nothing was suspended).
        private static readonly List<int> _suspended = new List<int>();
        private static RandomStream _pendingRootStream; // set while a replayed BulletScriptSource initialises

        // Host: replayed script sources → the id clients know them by.
        private static readonly Dictionary<BulletScriptSource, int> _hostSources = new Dictionary<BulletScriptSource, int>();
        private static int _nextScriptId = 1;
        private static readonly System.Random _seedSource = new System.Random();

        // Client: host script id → the source running it on our puppet.
        private readonly Dictionary<int, BulletScriptSource> _clientSources = new Dictionary<int, BulletScriptSource>();

        private float _nextSweepTime;

        public static bool IsReplayed(Bullet bullet) => bullet != null && _streams.ContainsKey(bullet);

        private static RandomStream NewStream(int seed)
        {
            Random.State saved = Random.state;
            Random.InitState(seed);
            var stream = new RandomStream { Seed = seed, State = Random.state };
            Random.state = saved;
            return stream;
        }

        // ---- Entry points for the Harmony patches ----

        /// <summary>BulletScriptSource.Initialize prefix (host): decide whether this script is replayed.</summary>
        public static void BeginSourceInit(BulletScriptSource source)
        {
            if (!NetworkSession.Instance.IsHost || !Enabled || source == null || source.BulletScript == null) return;

            AIBulletBank bank = source.BulletManager != null ? source.BulletManager : source.bulletBank;
            // Any synced enemy, not just bosses: regular enemies' scripts (book casters, chain bullets...)
            // spawn bullets that stand still in a pattern and only later get their speed, which the
            // straight-line copies (4a) never saw - they hung in the air on the client.
            AIActor enemy = bank != null ? bank.aiActor : null;
            if (enemy == null) return;
            if (!NetworkEntityManager.Instance.TryGetId(enemy, out int enemyId)) return;

            int seed = _seedSource.Next(1, int.MaxValue);
            int scriptId = _nextScriptId++;
            _pendingRootStream = NewStream(seed);
            _hostSources[source] = scriptId;

            Vector3 offset = source.transform.position - enemy.transform.position;
            NetworkSession.Instance.Broadcast(new BossScriptStartPacket
            {
                EnemyId = enemyId,
                ScriptId = scriptId,
                ScriptTypeName = source.BulletScript.scriptTypeName,
                Offset = offset,
                Rotation = source.transform.eulerAngles.z,
                Seed = seed
            }, reliable: true);
            Debug.Log($"[ScriptReplicator] Enemy {enemyId} started {source.BulletScript.scriptTypeName} (script {scriptId}, seed {seed}).");
        }

        /// <summary>BulletScriptSource.Initialize finalizer: the pending stream is only for this call.</summary>
        public static void EndSourceInit() => _pendingRootStream = null;

        /// <summary>BulletScriptSelector.CreateInstance postfix: the root bullet of a script now exists.</summary>
        public static void OnRootCreated(Bullet root)
        {
            if (_pendingRootStream == null || root == null) return;
            _streams[root] = _pendingRootStream;
            _pendingRootStream = null;
        }

        /// <summary>BulletSpawnedHandler prefix: a replayed bullet fired a child - give it a derived stream.</summary>
        public static void OnChildSpawned(Bullet child)
        {
            if (child == null || _frames.Count == 0) return;
            RandomStream parent = _frames[_frames.Count - 1].Stream;
            if (parent == null) return;

            // Depends only on the parent's seed and firing order - identical on host and client.
            int childSeed = unchecked(parent.Seed * 31 + ++parent.FireCount);
            _streams[child] = NewStream(childSeed);
        }

        /// <summary>
        /// BulletSpawnedHandler prefix, after OnChildSpawned: hand UnityEngine.Random back to the game
        /// for the spawn itself. Spawning (projectile pools, muzzle VFX, audio) may draw random numbers
        /// differently on host and client; those draws must not shift the parent script's stream.
        /// The child's own Initialize, called during the spawn, still swaps in the child's stream.
        /// </summary>
        public static void SuspendStream()
        {
            int top = _frames.Count - 1;
            if (top < 0 || _frames[top].Stream == null)
            {
                _suspended.Add(-1);
                return;
            }
            Frame frame = _frames[top];
            frame.Stream.State = Random.state;
            Random.state = frame.Saved;
            _suspended.Add(top);
        }

        /// <summary>BulletSpawnedHandler finalizer: swap the parent's stream back in.</summary>
        public static void ResumeStream()
        {
            if (_suspended.Count == 0) return;
            int index = _suspended[_suspended.Count - 1];
            _suspended.RemoveAt(_suspended.Count - 1);
            if (index < 0 || index >= _frames.Count) return;

            Frame frame = _frames[index];
            frame.Saved = Random.state; // the game's own sequence moved on during the spawn
            _frames[index] = frame;
            Random.state = frame.Stream.State;
        }

        /// <summary>Bullet.Initialize / FrameUpdate prefix: swap in this bullet's stream, if replayed.</summary>
        public static void EnterBullet(Bullet bullet)
        {
            if (bullet == null || !_streams.TryGetValue(bullet, out RandomStream stream))
            {
                // Still push, so every Exit pops its own Enter; and so a non-replayed bullet running
                // inside a replayed one doesn't inherit its stream for child spawns.
                _frames.Add(new Frame { Bullet = bullet });
                return;
            }
            _frames.Add(new Frame { Bullet = bullet, Stream = stream, Saved = Random.state });
            Random.state = stream.State;
        }

        /// <summary>Bullet.Initialize / FrameUpdate finalizer.</summary>
        public static void ExitBullet()
        {
            if (_frames.Count == 0) return;
            Frame frame = _frames[_frames.Count - 1];
            _frames.RemoveAt(_frames.Count - 1);
            if (frame.Stream == null) return;
            frame.Stream.State = Random.state;
            Random.state = frame.Saved;
        }

        /// <summary>BulletScriptSource.ForceStop prefix (host): tell clients to cut theirs short too.</summary>
        public static void OnSourceStopped(BulletScriptSource source)
        {
            if (!NetworkSession.Instance.IsHost || source == null || source.RootBullet == null) return;
            if (!_hostSources.TryGetValue(source, out int scriptId)) return;
            _hostSources.Remove(source);
            NetworkSession.Instance.Broadcast(new BossScriptStopPacket { ScriptId = scriptId }, reliable: true);
        }

        // ---- Client ----

        public void HandleStart(BossScriptStartPacket packet)
        {
            GameObject remote = NetworkEntityManager.Instance.GetRemote(packet.EnemyId);
            AIActor puppet = remote != null ? remote.GetComponent<AIActor>() : null;
            if (puppet == null || puppet.bulletBank == null || string.IsNullOrEmpty(packet.ScriptTypeName)) return;

            // A child object of the puppet stands in for the host's script source (often a shoot point).
            var sourceObject = new GameObject("GungeonTogether_BossScript_" + packet.ScriptId);
            sourceObject.transform.parent = puppet.transform;
            sourceObject.transform.position = puppet.transform.position + (Vector3)packet.Offset;
            sourceObject.transform.rotation = Quaternion.Euler(0f, 0f, packet.Rotation);

            BulletScriptSource source = sourceObject.AddComponent<BulletScriptSource>();
            source.BulletManager = puppet.bulletBank;
            source.BulletScript = new BulletScriptSelector { scriptTypeName = packet.ScriptTypeName };

            _pendingRootStream = NewStream(packet.Seed);
            try
            {
                source.Initialize();
            }
            catch (System.Exception e)
            {
                Debug.LogWarningThrottled($"Script.StartFailed:{packet.ScriptTypeName}",
                    $"[ScriptReplicator] Couldn't replay {packet.ScriptTypeName} on enemy {packet.EnemyId}: {e.Message}");
                Object.Destroy(sourceObject);
                return;
            }
            finally
            {
                _pendingRootStream = null;
            }

            _clientSources[packet.ScriptId] = source;
            Debug.Log($"[ScriptReplicator] Replaying {packet.ScriptTypeName} on enemy {packet.EnemyId} (script {packet.ScriptId}).");
        }

        public void HandleStop(BossScriptStopPacket packet)
        {
            if (!_clientSources.TryGetValue(packet.ScriptId, out BulletScriptSource source)) return;
            _clientSources.Remove(packet.ScriptId);
            if (source == null) return;
            source.ForceStop();
            Object.Destroy(source.gameObject);
        }

        // ---- Housekeeping ----

        private void Update()
        {
            if (Time.realtimeSinceStartup < _nextSweepTime) return;
            _nextSweepTime = Time.realtimeSinceStartup + SweepInterval;

            // Forget streams of bullets that are done (no ConditionalWeakTable on net35).
            if (_streams.Count > 0)
            {
                var ended = new List<Bullet>();
                foreach (Bullet bullet in _streams.Keys)
                {
                    if (bullet.Destroyed || bullet.IsEnded) ended.Add(bullet);
                }
                foreach (Bullet bullet in ended) _streams.Remove(bullet);
            }

            if (_hostSources.Count > 0)
            {
                var ended = new List<BulletScriptSource>();
                foreach (BulletScriptSource source in _hostSources.Keys)
                {
                    if (source == null || source.IsEnded) ended.Add(source);
                }
                foreach (BulletScriptSource source in ended) _hostSources.Remove(source);
            }

            if (_clientSources.Count > 0)
            {
                var ended = new List<int>();
                foreach (var kv in _clientSources)
                {
                    if (kv.Value == null || kv.Value.IsEnded) ended.Add(kv.Key);
                }
                foreach (int id in ended)
                {
                    if (_clientSources[id] != null) Object.Destroy(_clientSources[id].gameObject);
                    _clientSources.Remove(id);
                }
            }
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState()
        {
            foreach (BulletScriptSource source in _clientSources.Values)
            {
                if (source != null) Object.Destroy(source.gameObject);
            }
            _clientSources.Clear();
            _hostSources.Clear();
            _streams.Clear();
            _frames.Clear();
            _suspended.Clear();
            _pendingRootStream = null;
        }
    }
}
