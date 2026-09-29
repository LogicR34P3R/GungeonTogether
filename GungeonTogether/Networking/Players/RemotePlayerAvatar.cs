using System.Collections.Generic;
using UnityEngine;
using GungeonTogether.Networking.Packets;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Players
{
    /// <summary>
    /// Stand-in for another player: a tk2d sprite from that player's own character prefab, showing
    /// whichever sprite frame their game is currently displaying (sent 20x/s with the position).
    /// Showing the sender's live frame rather than running our own animator mirrors every clip -
    /// walk, idle, dodge roll - without syncing animation state separately.
    /// </summary>
    public class RemotePlayerAvatar : MonoBehaviour
    {
        private const int UnknownCharacter = -1;

        // Character prefab sprite per PlayableCharacters value - loading a prefab per avatar rebuild is wasteful.
        private static readonly Dictionary<int, tk2dBaseSprite> _prefabSprites = new Dictionary<int, tk2dBaseSprite>();

        // Snapshot interpolation: render this far behind the sender's newest state, blending between
        // the two snapshots around that moment. Updates arrive unevenly (frame-quantised sends plus
        // network jitter); easing towards "the latest one" turned that into visible stutter, while
        // interpolating on the sender's own timestamps moves at an even pace. 100 ms spans three
        // 30 Hz snapshots, so one late or lost packet doesn't stall the avatar.
        private const float InterpolationDelay = 0.1f;
        private const float SnapDistance = 4f; // a jump this big is a teleport, not movement - don't slide
        private const int MaxSnapshots = 32;

        // Clock mapping (local time minus sender time): the smallest arrival delay seen over the
        // last one to two windows. The render clock slews towards it at most this fast, so a change
        // never shows as a jump; only a large one (a hitch, a reconnect) is snapped.
        private const float ClockWindow = 1f;
        private const float ClockSlewRate = 0.05f; // seconds per second: plays at most 5% fast/slow
        private const float ClockSnapThreshold = 0.5f;

        // Live player sprites sit on whole pixels (the physics engine moves in 1/16-unit steps).
        // An avatar between pixels rounds differently each frame as the camera moves, and shimmers.
        private const float PixelsPerUnit = 16f;

        private static readonly Color GhostTint = new Color(0.45f, 0.55f, 1f, 1f);

        private struct Snapshot
        {
            public float Time; // sender clock
            public Vector2 Position;
            public float Rotation;
            public bool FlipX;
            public Vector2 SpriteOffset;
            public int SpriteId;
            public int GunId;
            public int GunSpriteId;
            public Vector2 GunOffset;
            public float GunAngle;
            public bool GunFlipY;
            public float GunHeight;
            public bool GunVisible;
        }

        private readonly List<Snapshot> _snapshots = new List<Snapshot>();
        private float _clockOffset;      // what rendering uses
        private float _clockTarget;      // min delay over the current and previous window
        private float _windowMin, _previousWindowMin;
        private float _windowStart;
        private bool _hasClockOffset;

        private tk2dSprite _sprite;
        private Transform _spriteTransform;
        private Color _baseColor = Color.white;
        private bool _isGhost;
        private Vector3 _targetPosition;
        private int _characterId = UnknownCharacter;
        private bool _altCostume;
        private int _spriteId = -1;
        private bool _flipX;

        private tk2dSprite _gunSprite;
        private int _gunId = -1;

        /// <summary>See RemotePlayerTarget.</summary>
        public RemotePlayerTarget EnemyTarget { get; private set; }

        public void EnableEnemyTarget(ulong steamId)
        {
            if (EnemyTarget == null) EnemyTarget = RemotePlayerTarget.Create(transform, steamId);
        }

        // Last known stats for this remote player. Nothing renders these yet (no remote HUD
        // exists), but they belong here - on the specific remote player they describe - rather
        // than being applied to whichever PlayerController happens to be local.
        public float Health { get; private set; }
        public float MaxHealth { get; private set; }
        public float Armor { get; private set; }
        public float MaxArmor { get; private set; }
        public int Ammo { get; private set; }
        public int MaxAmmo { get; private set; }
        public int CurrentGunIndex { get; private set; }
        public string ActiveItemName { get; private set; }

        public bool HasSprite => _sprite != null;

        /// <summary>Newest position received over the network - ahead of what's drawn (see InterpolationDelay).</summary>
        public Vector2 NetworkPosition => _targetPosition;
        public float LastUpdateTime { get; private set; }

        public static RemotePlayerAvatar Create(ulong steamId, Vector2 position, float rotation)
        {
            GameObject go = new GameObject("RemotePlayer_" + steamId);
            RemotePlayerAvatar avatar = go.AddComponent<RemotePlayerAvatar>();
            avatar.transform.position = new Vector3(position.x, position.y, 0f);
            avatar.transform.rotation = Quaternion.Euler(0f, 0f, rotation);
            avatar._targetPosition = avatar.transform.position;
            return avatar;
        }

        public void Apply(PlayerPositionPacket packet)
        {
            float now = Time.realtimeSinceStartup;
            LastUpdateTime = now;
            TrackClock(now, now - packet.SendTime);

            // Unreliable packets can arrive out of order; the stale ones add nothing.
            if (_snapshots.Count > 0 && packet.SendTime <= _snapshots[_snapshots.Count - 1].Time) return;

            _snapshots.Add(new Snapshot
            {
                Time = packet.SendTime,
                Position = packet.Position,
                Rotation = packet.Rotation,
                FlipX = packet.FlipX,
                SpriteOffset = packet.SpriteOffset,
                SpriteId = packet.SpriteId,
                GunId = packet.GunId,
                GunSpriteId = packet.GunSpriteId,
                GunOffset = packet.GunOffset,
                GunAngle = packet.GunAngle,
                GunFlipY = packet.GunFlipY,
                GunHeight = packet.GunHeight,
                GunVisible = packet.GunVisible
            });
            if (_snapshots.Count > MaxSnapshots) _snapshots.RemoveAt(0);
            _targetPosition = new Vector3(packet.Position.x, packet.Position.y, 0f);

            if (packet.CharacterId != _characterId || packet.AltCostume != _altCostume)
            {
                // First packet, or they picked another character or costume in the Breach: rebuild.
                _characterId = packet.CharacterId;
                _altCostume = packet.AltCostume;
                DestroySprite();
            }
            if (_sprite == null) TryCreateSprite();
        }

        /// <summary>
        /// The least-delayed packet gives the best local↔sender clock mapping. The old approach -
        /// jump down to any new minimum, creep up otherwise - made a sawtooth: every few seconds the
        /// avatar skipped ahead by the network jitter. A windowed minimum and a rate-limited render
        /// clock follow drift and latency changes without ever jumping.
        /// </summary>
        private void TrackClock(float now, float delay)
        {
            if (!_hasClockOffset)
            {
                _hasClockOffset = true;
                _windowStart = now;
                _windowMin = _previousWindowMin = _clockTarget = _clockOffset = delay;
                return;
            }

            if (now - _windowStart >= ClockWindow)
            {
                _previousWindowMin = _windowMin;
                _windowMin = delay;
                _windowStart = now;
            }
            else if (delay < _windowMin)
            {
                _windowMin = delay;
            }
            _clockTarget = Mathf.Min(_windowMin, _previousWindowMin);
        }

        private void AdvanceClock()
        {
            float error = _clockTarget - _clockOffset;
            if (Mathf.Abs(error) > ClockSnapThreshold) _clockOffset = _clockTarget;
            else _clockOffset += Mathf.Clamp(error, -ClockSlewRate * Time.unscaledDeltaTime, ClockSlewRate * Time.unscaledDeltaTime);
        }

        /// <summary>Co-op ghost look while that player is dead and spectating.</summary>
        public void SetGhost(bool isGhost)
        {
            if (_isGhost == isGhost) return;
            _isGhost = isGhost;
            ApplyTint();
        }

        private void ApplyTint()
        {
            if (_sprite != null) _sprite.color = _isGhost ? GhostTint : _baseColor;
        }

        public void ApplyState(PlayerStatePacket packet)
        {
            Health = packet.Health;
            MaxHealth = packet.MaxHealth;
            Armor = packet.Armor;
            MaxArmor = packet.MaxArmor;
            Ammo = packet.Ammo;
            MaxAmmo = packet.MaxAmmo;
            CurrentGunIndex = packet.CurrentGunIndex;
            ActiveItemName = packet.ActiveItemName;
        }

        private void Update()
        {
            if (_sprite == null) TryCreateSprite();
            if (_snapshots.Count == 0) return;

            AdvanceClock();
            Snapshot shown = Sample(Time.realtimeSinceStartup - _clockOffset - InterpolationDelay, out Vector2 position);
            position = SnapToPixel(position);
            transform.position = new Vector3(position.x, position.y, 0f);
            transform.rotation = Quaternion.Euler(0f, 0f, shown.Rotation);
            if (EnemyTarget != null) EnemyTarget.SyncPosition();
            _flipX = shown.FlipX;
            _spriteId = shown.SpriteId; // the frame from the same moment, so animation matches movement
            // Same moment as the flip it belongs to; the prefab's fixed offset made the avatar jump
            // sideways whenever the sender turned to aim the other way. Facing right the offset is
            // exactly zero, so it must be applied as-is (skipping zero left the sprite shifted).
            if (_spriteTransform != null)
            {
                _spriteTransform.localPosition = new Vector3(shown.SpriteOffset.x, shown.SpriteOffset.y, _spriteTransform.localPosition.z);
            }
            ApplyFrame();

            // ETG draws by z = y - HeightOffGround (tilted world). Left at z = 0 the sprite sits far
            // off the level's depth range and is never drawn, so re-derive it after every move.
            if (_sprite != null) _sprite.UpdateZDepth();
            UpdateGun(shown, position);
        }

        private static Vector2 SnapToPixel(Vector2 p) =>
            new Vector2(Mathf.Round(p.x * PixelsPerUnit) / PixelsPerUnit, Mathf.Round(p.y * PixelsPerUnit) / PixelsPerUnit);

        // ---- Held gun ----

        private void UpdateGun(Snapshot shown, Vector2 position)
        {
            if (shown.GunId != _gunId)
            {
                _gunId = shown.GunId;
                DestroyGun();
                TryCreateGun();
            }
            if (_gunSprite == null) return;

            Renderer gunRenderer = _gunSprite.GetComponent<Renderer>();
            if (gunRenderer != null) gunRenderer.enabled = shown.GunVisible;
            if (!shown.GunVisible) return;

            _gunSprite.transform.position = new Vector3(position.x + shown.GunOffset.x, position.y + shown.GunOffset.y, 0f);
            _gunSprite.transform.rotation = Quaternion.Euler(0f, 0f, shown.GunAngle);
            _gunSprite.FlipY = shown.GunFlipY;
            _gunSprite.HeightOffGround = shown.GunHeight;

            // Bounds-checked like the body's frame: a gun can switch to another collection.
            tk2dSpriteCollectionData collection = _gunSprite.Collection;
            if (shown.GunSpriteId >= 0 && collection != null && collection.spriteDefinitions != null
                && shown.GunSpriteId < collection.spriteDefinitions.Length && shown.GunSpriteId != _gunSprite.spriteId)
            {
                _gunSprite.SetSprite(shown.GunSpriteId);
            }
            _gunSprite.UpdateZDepth();
        }

        private void TryCreateGun()
        {
            if (_gunId < 0) return;
            Gun prefab = PickupObjectDatabase.GetById(_gunId) as Gun;
            tk2dBaseSprite source = prefab != null ? prefab.GetComponent<tk2dBaseSprite>() : null;
            if (source == null) return;

            GameObject gunObject = new GameObject("Gun");
            int playerLayer = LayerMask.NameToLayer("FG_Reflection"); // see TryCreateSprite
            gunObject.layer = playerLayer >= 0 ? playerLayer : source.gameObject.layer;
            gunObject.transform.parent = transform;

            _gunSprite = gunObject.AddComponent<tk2dSprite>();
            _gunSprite.SetSprite(source.Collection, source.spriteId);
            _gunSprite.scale = source.scale;
            _gunSprite.IsPerpendicular = source.IsPerpendicular;
        }

        private void DestroyGun()
        {
            if (_gunSprite != null) Destroy(_gunSprite.gameObject);
            _gunSprite = null;
        }

        /// <summary>
        /// The state at sender time t: position blended between the snapshots either side of it,
        /// discrete fields (frame, facing) from the older one. Before the buffer: the oldest; past
        /// it (packets late/stopped): hold the newest rather than guess.
        /// </summary>
        private Snapshot Sample(float t, out Vector2 position)
        {
            int last = _snapshots.Count - 1;
            if (t >= _snapshots[last].Time)
            {
                position = _snapshots[last].Position;
                return _snapshots[last];
            }
            if (t <= _snapshots[0].Time)
            {
                position = _snapshots[0].Position;
                return _snapshots[0];
            }

            int i = last - 1;
            while (i > 0 && _snapshots[i].Time > t) i--;
            Snapshot a = _snapshots[i], b = _snapshots[i + 1];

            if (Vector2.Distance(a.Position, b.Position) > SnapDistance)
            {
                position = b.Position;
                return b;
            }
            float span = b.Time - a.Time;
            float f = span > 0f ? (t - a.Time) / span : 1f;
            position = Vector2.Lerp(a.Position, b.Position, f);

            // Drop snapshots older than the pair in use - they can never be needed again.
            if (i > 0) _snapshots.RemoveRange(0, i);
            return a;
        }

        private void ApplyFrame()
        {
            if (_sprite == null) return;
            _sprite.FlipX = _flipX;

            // Bounds-checked: an alternate costume draws from another collection, whose ids can
            // exceed the prefab's. Keep the last good frame rather than throwing every packet.
            tk2dSpriteCollectionData collection = _sprite.Collection;
            if (_spriteId < 0 || collection == null || collection.spriteDefinitions == null
                || _spriteId >= collection.spriteDefinitions.Length || _spriteId == _sprite.spriteId)
            {
                return;
            }
            _sprite.SetSprite(_spriteId);
        }

        /// <summary>
        /// Builds the sprite from the remote player's character prefab. Until their character is
        /// known, falls back to copying the local player's sprite; during a level load neither may
        /// exist yet, so Update() keeps retrying rather than leaving the avatar invisible.
        /// </summary>
        private void TryCreateSprite()
        {
            tk2dBaseSprite source = GetPrefabSprite(_characterId);
            if (source == null)
            {
                PlayerController localPlayer = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
                source = localPlayer != null ? localPlayer.sprite : null;
            }
            if (source == null) return;

            // The sprite sits under the player root (sometimes via PlayerRotatePoint); keep its offset.
            Transform sourceRoot = source.transform.root;

            GameObject spriteObject = new GameObject("Sprite");
            // The layer live player sprites end up on. tk2dBaseSprite.Awake moves any sprite with a
            // gameActor to FG_Reflection at runtime, but a prefab never ran Awake and still carries
            // FG_Critical, which ETG draws in another pass - the avatar looked washed out/ghostly.
            // A new object would land on Default, which ETG's cameras skip entirely.
            int playerLayer = LayerMask.NameToLayer("FG_Reflection");
            spriteObject.layer = playerLayer >= 0 ? playerLayer : source.gameObject.layer;
            spriteObject.transform.parent = transform;
            spriteObject.transform.localPosition = sourceRoot.InverseTransformPoint(source.transform.position);
            spriteObject.transform.localRotation = Quaternion.identity;
            spriteObject.transform.localScale = source.transform.lossyScale;
            _spriteTransform = spriteObject.transform;

            _sprite = spriteObject.AddComponent<tk2dSprite>();
            tk2dSpriteCollectionData costume = _altCostume ? GetAltCostumeCollection(source) : null;
            if (costume != null) _sprite.SetSprite(costume, 0);
            else _sprite.SetSprite(source.Collection, source.spriteId);
            _sprite.HeightOffGround = source.HeightOffGround;
            _sprite.SortingOrder = source.SortingOrder;
            _sprite.scale = source.scale;
            _baseColor = source.color;
            _sprite.IsPerpendicular = source.IsPerpendicular;
            _sprite.depthUsesTrimmedBounds = source.depthUsesTrimmedBounds;
            ApplyTint();
            ApplyFrame();
            _sprite.UpdateZDepth();

            Debug.LogInfo($"[RemotePlayer] {name} sprite created: character={(_characterId == UnknownCharacter ? "unknown (local copy)" : ((PlayableCharacters)_characterId).ToString())}, " +
                          $"costume={(_altCostume ? (costume != null ? "alternate" : "alternate (not found, base used)") : "base")}, " +
                          $"layer={LayerMask.LayerToName(spriteObject.layer)}, pos={_spriteTransform.position}");
        }

        private void DestroySprite()
        {
            if (_spriteTransform != null) Destroy(_spriteTransform.gameObject);
            _sprite = null;
            _spriteTransform = null;
        }

        /// <summary>
        /// The collection of the character's Wardrobe costume: the one its AlternateCostumeLibrary's
        /// clips draw from (as PlayerController.SwapToAlternateCostume finds it for the hands).
        /// </summary>
        private static tk2dSpriteCollectionData GetAltCostumeCollection(tk2dBaseSprite prefabSprite)
        {
            PlayerController prefab = prefabSprite.transform.root.GetComponent<PlayerController>();
            tk2dSpriteAnimation library = prefab != null ? prefab.AlternateCostumeLibrary : null;
            if (library == null || library.clips == null) return null;
            foreach (tk2dSpriteAnimationClip clip in library.clips)
            {
                if (clip != null && clip.frames != null && clip.frames.Length > 0 && clip.frames[0].spriteCollection != null)
                {
                    return clip.frames[0].spriteCollection;
                }
            }
            return null;
        }

        /// <summary>The sprite on a character's prefab, found the way PlayerController.Awake finds it.</summary>
        private static tk2dBaseSprite GetPrefabSprite(int characterId)
        {
            if (characterId == UnknownCharacter) return null;
            if (_prefabSprites.TryGetValue(characterId, out tk2dBaseSprite cached) && cached != null) return cached;

            string path = CharacterSelectController.GetCharacterPathFromIdentity((PlayableCharacters)characterId);
            GameObject prefab = BraveResources.Load(path) as GameObject;
            if (prefab == null)
            {
                Debug.LogWarning($"[RemotePlayer] Could not load character prefab {path}.");
                return null;
            }

            Transform spriteTransform = prefab.transform.Find("PlayerSprite")
                ?? prefab.transform.Find("PlayerRotatePoint/PlayerSprite");
            tk2dBaseSprite sprite = spriteTransform != null ? spriteTransform.GetComponent<tk2dBaseSprite>() : null;
            if (sprite == null)
            {
                Debug.LogWarning($"[RemotePlayer] Character prefab {path} has no PlayerSprite.");
                return null;
            }

            _prefabSprites[characterId] = sprite;
            return sprite;
        }
    }
}
