using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Dungeonator;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;
using Object = UnityEngine.Object;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Keeps room props the same on both sides: flipped tables, broken pots, crates, barrels and
    /// tables, and where movable things are - pushed tables, kicked barrels, minecarts. Either side
    /// reports its own (postfixes in RoomObjectPatches; movement is polled), the host relays, and
    /// the other side finds the same object by position and flips/breaks/moves it.
    ///
    /// A mirrored break is only the look of it: the break event (which carries loot drops and
    /// explosions), item spawns and pot fairies are switched off first. They already happened on
    /// the side that broke it, and loot reaches the other side through LootReplicator.
    /// </summary>
    public class RoomObjectReplicator : MonoSingleton<RoomObjectReplicator>
    {
        // The same prop on both sides sits at the same spot; allow float noise, not a neighbour.
        private const float MatchDistance = 0.5f;

        // Set while applying a remote event, so the postfixes don't report it straight back.
        private static bool _applying;

        // ---- Movable objects ----
        // There's no event for something sliding, rolling or riding along, so positions are polled.
        // Each object is identified by its kind and where it started - the same spot on both sides.

        public enum MovableKind : byte
        {
            Table = 0,     // FlippableCover: pushed once flipped
            Kickable = 1,  // KickableObject: barrels and the like, kicked into a roll
            MineCart = 2   // MineCartController: pushed or ridden along its rails
        }

        private class Movable
        {
            public Component Object;
            public MovableKind Kind;
            public Vector2 Home;
            public int Serial;          // with Home: the identity (see RoomObjectPacket.Serial)
            public Vector2 LastKnown;
            public string LastClip = "";
            // Remote-driven: eased towards Target, and not reported back while it is.
            public Vector2 Target;
            public float DrivenUntil;
            public bool AlwaysDriven;   // a client's copy of a host factory cart: only ever follows the host
        }

        private static tk2dSpriteAnimator AnimatorOf(Component o)
        {
            BraveBehaviour behaviour = o as BraveBehaviour;
            return behaviour != null ? behaviour.spriteAnimator : o.GetComponent<tk2dSpriteAnimator>();
        }

        private const float MoveCheckInterval = 0.1f;
        private const float MoveThreshold = 1f / 16f;    // one pixel
        private const float DrivenHoldSeconds = 0.5f;    // after the last remote update, it's ours again
        private const float FollowRate = 15f;            // easing towards the remote position, per second
        private const float SnapDistance = 4f;

        private readonly List<Movable> _movables = new List<Movable>();
        private Dungeon _movablesOf;
        private float _nextMoveCheck;

        private void Update()
        {
            GameManager gm = GameManager.HasInstance ? GameManager.Instance : null;
            if (gm == null || gm.IsLoadingLevel || gm.IsFoyer || gm.Dungeon == null || !NetworkSession.Instance.IsConnected) return;
            if (_movablesOf != gm.Dungeon) CacheMovables(gm.Dungeon);

            float now = Time.realtimeSinceStartup;
            ScanMovables(now);
            FollowRemote(now);

            if (now < _nextMoveCheck) return;
            _nextMoveCheck = now + MoveCheckInterval;
            foreach (Movable m in _movables)
            {
                if (m.Object == null || m.AlwaysDriven || now < m.DrivenUntil) continue;
                Vector2 position = m.Object.transform.position;
                tk2dSpriteAnimator animator = AnimatorOf(m.Object);
                string clip = animator != null && animator.CurrentClip != null ? animator.CurrentClip.name ?? "" : "";
                // A change of animation counts too: a barrel that stops rolling doesn't move any more.
                if (Vector2.Distance(position, m.LastKnown) < MoveThreshold && clip == m.LastClip) continue;
                m.LastKnown = position;
                m.LastClip = clip;
                Send(new RoomObjectPacket
                {
                    Event = RoomObjectPacket.ObjectEvent.ObjectMoved,
                    MovableKind = (byte)m.Kind,
                    Position = m.Home,
                    Serial = m.Serial,
                    MovedTo = position,
                    Clip = clip
                });
            }
        }

        /// <summary>Eases remote-driven objects towards their latest position (updates come at 10 Hz).</summary>
        private void FollowRemote(float now)
        {
            foreach (Movable m in _movables)
            {
                if (m.Object == null || (!m.AlwaysDriven && now >= m.DrivenUntil)) continue;
                if (m.AlwaysDriven)
                {
                    // Its own rail logic mustn't move it between the host's updates.
                    SpeculativeRigidbody body = m.Object.GetComponent<SpeculativeRigidbody>();
                    if (body != null) body.Velocity = Vector2.zero;
                }
                Vector2 current = m.Object.transform.position;
                if (Vector2.Distance(current, m.Target) < 0.01f) continue;
                Vector2 next = Vector2.Distance(current, m.Target) > SnapDistance
                    ? m.Target
                    : Vector2.Lerp(current, m.Target, Mathf.Clamp01(Time.deltaTime * FollowRate));
                SetPosition(m, next);
            }
        }

        private static void SetPosition(Movable m, Vector2 position)
        {
            Transform t = m.Object.transform;
            t.position = new Vector3(position.x, position.y, t.position.z);
            // The rigidbody keeps its own position and only follows the transform on Reinitialize.
            SpeculativeRigidbody body = m.Object.GetComponent<SpeculativeRigidbody>();
            if (body != null) body.Reinitialize();
            tk2dBaseSprite sprite = m.Object.GetComponent<tk2dBaseSprite>();
            if (sprite != null) sprite.UpdateZDepth();
            m.LastKnown = position;
        }

        /// <summary>A new level: forget the old one's objects; ScanMovables finds the new ones.</summary>
        private void CacheMovables(Dungeon dungeon)
        {
            _movablesOf = dungeon;
            _movables.Clear();
            _known.Clear();
            _pendingMoves.Clear();
            _pendingFlips.Clear();
            _cartSerials.Clear();
            _nextScan = 0f;
        }

        // Rooms keep their props under a "Room_..." object the game switches off while the room isn't
        // visible, and FindObjectsOfType skips switched-off objects - a single scan at level load
        // only ever found the first room's props, so nothing else synced. So rescan every second and
        // add whatever just came into view. An object in a room nobody has seen can't have moved
        // yet, so where it is at first sight is still its starting spot, the same on both sides.
        private const float ScanInterval = 1f;
        private readonly HashSet<Component> _known = new HashSet<Component>();
        private float _nextScan;

        private void ScanMovables(float now)
        {
            if (now < _nextScan) return;
            _nextScan = now + ScanInterval;
            foreach (FlippableCover o in Object.FindObjectsOfType<FlippableCover>()) AddIfNew(o, MovableKind.Table);
            foreach (KickableObject o in Object.FindObjectsOfType<KickableObject>()) AddIfNew(o, MovableKind.Kickable);
            foreach (MineCartController o in Object.FindObjectsOfType<MineCartController>()) AddIfNew(o, MovableKind.MineCart);
        }

        private void AddIfNew(Component o, MovableKind kind)
        {
            if (!_known.Contains(o)) AddMovable(o, kind);
        }

        private Movable AddMovable(Component o, MovableKind kind, Vector2? home = null, int serial = 0)
        {
            _known.Add(o);
            Vector2 start = home ?? (Vector2)o.transform.position;
            var m = new Movable { Object = o, Kind = kind, Home = start, Serial = serial, LastKnown = o.transform.position, Target = o.transform.position };
            _movables.Add(m);
            ApplyPending(m);
            return m;
        }

        // Events for objects in rooms still switched off here (not found yet), newest per object.
        private readonly List<RoomObjectPacket> _pendingMoves = new List<RoomObjectPacket>();
        private readonly List<RoomObjectPacket> _pendingFlips = new List<RoomObjectPacket>();

        private static bool SameObject(Movable m, RoomObjectPacket p) =>
            (byte)m.Kind == p.MovableKind && m.Serial == p.Serial && Vector2.Distance(m.Home, p.Position) <= MatchDistance;

        private void Hold(List<RoomObjectPacket> pending, RoomObjectPacket packet, System.Predicate<RoomObjectPacket> sameAs)
        {
            pending.RemoveAll(sameAs);
            pending.Add(packet);
        }

        private void ApplyPending(Movable m)
        {
            for (int i = _pendingFlips.Count - 1; i >= 0; i--)
            {
                if (m.Kind != MovableKind.Table || Vector2.Distance(m.Home, _pendingFlips[i].Position) > MatchDistance) continue;
                FlippableCover table = m.Object as FlippableCover;
                if (table != null && !table.IsFlipped)
                {
                    _applying = true;
                    try { table.Flip((DungeonData.Direction)_pendingFlips[i].FlipDirection); }
                    finally { _applying = false; }
                }
                _pendingFlips.RemoveAt(i);
            }
            for (int i = _pendingMoves.Count - 1; i >= 0; i--)
            {
                if (!SameObject(m, _pendingMoves[i])) continue;
                SetPosition(m, _pendingMoves[i].MovedTo); // it was moved while out of view here: just put it there
                _pendingMoves.RemoveAt(i);
            }
        }

        private void MoveObject(RoomObjectPacket packet)
        {
            Movable match = null;
            float best = MatchDistance;
            foreach (Movable m in _movables)
            {
                if (m.Object == null || (byte)m.Kind != packet.MovableKind || m.Serial != packet.Serial) continue;
                float distance = Vector2.Distance(m.Home, packet.Position);
                if (distance <= best)
                {
                    match = m;
                    best = distance;
                }
            }
            if (match == null)
            {
                // Its room is still switched off here; put it in place once it shows up (ApplyPending).
                Hold(_pendingMoves, packet, p => p.MovableKind == packet.MovableKind && p.Serial == packet.Serial && Vector2.Distance(p.Position, packet.Position) <= MatchDistance);
                return;
            }

            match.Target = packet.MovedTo;
            match.DrivenUntil = Time.realtimeSinceStartup + DrivenHoldSeconds;

            // Its animation too: a kicked barrel rolling, a cart's wheels.
            tk2dSpriteAnimator animator = AnimatorOf(match.Object);
            if (animator != null && !string.IsNullOrEmpty(packet.Clip)
                && (animator.CurrentClip == null || animator.CurrentClip.name != packet.Clip))
            {
                tk2dSpriteAnimationClip clip = animator.GetClipByName(packet.Clip);
                if (clip != null) animator.Play(clip);
            }
            match.LastClip = packet.Clip ?? "";
        }

        // ---- Minecart factories ----
        // A MineCartFactory keeps sending self-driving carts round a loop while a player is in its
        // room. Each side ran its own, so the carts never matched. Now only the host's factories make
        // carts; the client makes a copy when told and drives it from the host's positions.

        private static readonly FieldInfo SpawnedCartsField = typeof(MineCartFactory).GetField("m_spawnedCarts", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo DoSpawnCartMethod = typeof(MineCartFactory).GetMethod("DoSpawnCart", BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly Dictionary<MineCartFactory, int> _cartSerials = new Dictionary<MineCartFactory, int>();
        private static bool _spawningForHost;
        private static int _hostSerial;

        /// <summary>MineCartFactory.DoSpawnCart prefix: a client's factories only spawn on the host's word.</summary>
        public static bool AllowCartSpawn() => !NetworkSession.Instance.IsClient || _spawningForHost;

        /// <summary>MineCartFactory.DoSpawnCart postfix: track the new cart under (factory, serial).</summary>
        public static void OnCartSpawned(MineCartFactory factory)
        {
            if (!NetworkSession.Instance.IsConnected || factory == null) return;
            var carts = SpawnedCartsField != null ? SpawnedCartsField.GetValue(factory) as List<MineCartController> : null;
            MineCartController cart = carts != null && carts.Count > 0 ? carts[carts.Count - 1] : null;
            if (cart == null) return;

            RoomObjectReplicator self = Instance;
            Vector2 factoryPos = factory.transform.position;
            if (NetworkSession.Instance.IsHost)
            {
                self._cartSerials.TryGetValue(factory, out int serial);
                self._cartSerials[factory] = ++serial;
                self.AddMovable(cart, MovableKind.MineCart, factoryPos, serial);
                Send(new RoomObjectPacket { Event = RoomObjectPacket.ObjectEvent.CartSpawned, Position = factoryPos, Serial = serial });
            }
            else if (_spawningForHost)
            {
                Movable m = self.AddMovable(cart, MovableKind.MineCart, factoryPos, _hostSerial);
                m.AlwaysDriven = true;
                PathMover mover = cart.GetComponent<PathMover>();
                if (mover != null) mover.enabled = false;
            }
        }

        private static void SpawnCartForHost(RoomObjectPacket packet)
        {
            MineCartFactory factory = Nearest(Object.FindObjectsOfType<MineCartFactory>(), packet.Position, f => true);
            if (factory == null || DoSpawnCartMethod == null) return;
            _spawningForHost = true;
            _hostSerial = packet.Serial;
            try
            {
                DoSpawnCartMethod.Invoke(factory, null);
            }
            finally
            {
                _spawningForHost = false;
            }
        }

        // ---- Local events (postfixes) ----

        public static void OnTableFlipped(FlippableCover table, bool wasFlipped)
        {
            if (_applying || wasFlipped || table == null || !table.IsFlipped) return;
            Send(new RoomObjectPacket
            {
                Event = RoomObjectPacket.ObjectEvent.TableFlipped,
                Position = table.transform.position,
                FlipDirection = (int)table.DirectionFlipped
            });
        }

        public static void OnMinorBroken(MinorBreakable breakable, bool wasBroken, Vector2 direction)
        {
            if (_applying || wasBroken || breakable == null || !breakable.IsBroken) return;
            Send(new RoomObjectPacket
            {
                Event = RoomObjectPacket.ObjectEvent.MinorBroken,
                Position = breakable.transform.position,
                BreakDirection = direction
            });
        }

        public static void OnMajorBroken(MajorBreakable breakable, bool wasBroken, Vector2 direction)
        {
            if (_applying || wasBroken || breakable == null || !breakable.IsDestroyed) return;
            // Chests have their own sync (ChestReplicator); enemies and bosses die through EnemyReplicator.
            if (breakable.GetComponent<Chest>() != null || breakable.GetComponent<AIActor>() != null) return;
            Send(new RoomObjectPacket
            {
                Event = RoomObjectPacket.ObjectEvent.MajorBroken,
                Position = breakable.transform.position,
                BreakDirection = direction
            });
        }

        private static void Send(RoomObjectPacket packet)
        {
            GameManager gm = GameManager.HasInstance ? GameManager.Instance : null;
            if (gm == null || gm.IsLoadingLevel || gm.IsFoyer) return;
            if (NetworkSession.Instance.IsHost) NetworkSession.Instance.Broadcast(packet, reliable: true);
            else if (NetworkSession.Instance.IsClient) NetworkSession.Instance.SendToHost(packet, reliable: true);
        }

        // ---- Remote events ----

        public void HandleRoomObject(ulong senderId, RoomObjectPacket packet)
        {
            // The host passes a client's event on to the other clients.
            if (NetworkSession.Instance.IsHost) NetworkSession.Instance.Broadcast(packet, senderId, reliable: true);

            GameManager gm = GameManager.Instance;
            if (gm == null || gm.IsLoadingLevel || gm.IsFoyer) return;

            _applying = true;
            try
            {
                switch (packet.Event)
                {
                    case RoomObjectPacket.ObjectEvent.TableFlipped: FlipTable(packet); break;
                    case RoomObjectPacket.ObjectEvent.MinorBroken: BreakMinor(packet); break;
                    case RoomObjectPacket.ObjectEvent.MajorBroken: BreakMajor(packet); break;
                    case RoomObjectPacket.ObjectEvent.ObjectMoved: MoveObject(packet); break;
                    case RoomObjectPacket.ObjectEvent.CartSpawned: if (NetworkSession.Instance.IsClient) SpawnCartForHost(packet); break;
                }
            }
            finally
            {
                _applying = false;
            }
        }

        private void FlipTable(RoomObjectPacket packet)
        {
            FlippableCover table = Nearest(Object.FindObjectsOfType<FlippableCover>(), packet.Position, t => !t.IsFlipped);
            if (table == null)
            {
                // Its room is still switched off here (or it was already flipped): flip it once it shows up.
                Hold(_pendingFlips, packet, p => Vector2.Distance(p.Position, packet.Position) <= MatchDistance);
                return;
            }
            table.Flip((DungeonData.Direction)packet.FlipDirection);
        }

        private static void BreakMinor(RoomObjectPacket packet)
        {
            MinorBreakable breakable = Nearest(StaticReferenceManager.AllMinorBreakables, packet.Position, b => !b.IsBroken);
            if (breakable == null) return;
            breakable.OnBreak = null;
            breakable.OnBreakContext = null;
            breakable.canSpawnFairy = false;
            breakable.Break(packet.BreakDirection);
        }

        private static void BreakMajor(RoomObjectPacket packet)
        {
            MajorBreakable breakable = Nearest(StaticReferenceManager.AllMajorBreakables, packet.Position, b => !b.IsDestroyed);
            if (breakable == null) return;
            breakable.OnBreak = null;
            breakable.SpawnItemOnBreak = false;
            breakable.Break(packet.BreakDirection);
        }

        private static T Nearest<T>(System.Collections.Generic.IEnumerable<T> candidates, Vector2 position, Predicate<T> usable) where T : Component
        {
            T best = null;
            float bestDistance = MatchDistance;
            foreach (T candidate in candidates)
            {
                if (candidate == null || !usable(candidate)) continue;
                float distance = Vector2.Distance(candidate.transform.position, position);
                if (distance <= bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
            return best;
        }
    }
}
