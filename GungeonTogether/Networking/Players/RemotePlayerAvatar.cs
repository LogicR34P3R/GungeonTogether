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

        private struct Snapshot
        {
            public float Time; // sender clock
            public Vector2 Position;
            public float Rotation;
            public bool FlipX;
            public int SpriteId;
        }

        private readonly List<Snapshot> _snapshots = new List<Snapshot>();
        // Local time minus sender time, tracked from the fastest-arriving packets (see Apply).
        private float _clockOffset;
        private bool _hasClockOffset;

        private tk2dSprite _sprite;
        private Transform _spriteTransform;
        private Vector3 _targetPosition;
        private int _characterId = UnknownCharacter;
        private int _spriteId = -1;
        private bool _flipX;

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

        public void Apply(Vector2 position, float rotation, bool flipX, int characterId, int spriteId, float sendTime)
        {
            float now = Time.realtimeSinceStartup;
            LastUpdateTime = now;

            // The lowest (arrival - send) seen is the least-delayed packet: the best local↔sender
            // clock mapping. Creep towards later samples slowly, to follow clock drift and a
            // lasting latency change without reacting to a single late packet.
            float offset = now - sendTime;
            if (!_hasClockOffset || offset < _clockOffset) _clockOffset = offset;
            else _clockOffset += (offset - _clockOffset) * 0.02f;
            _hasClockOffset = true;

            // Unreliable packets can arrive out of order; the stale ones add nothing.
            if (_snapshots.Count > 0 && sendTime <= _snapshots[_snapshots.Count - 1].Time) return;

            _snapshots.Add(new Snapshot { Time = sendTime, Position = position, Rotation = rotation, FlipX = flipX, SpriteId = spriteId });
            if (_snapshots.Count > MaxSnapshots) _snapshots.RemoveAt(0);
            _targetPosition = new Vector3(position.x, position.y, 0f);

            if (characterId != _characterId)
            {
                // First packet, or they picked another character in the Breach: rebuild from that prefab.
                _characterId = characterId;
                DestroySprite();
            }
            if (_sprite == null) TryCreateSprite();
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

            Snapshot shown = Sample(Time.realtimeSinceStartup - _clockOffset - InterpolationDelay, out Vector2 position);
            transform.position = new Vector3(position.x, position.y, 0f);
            transform.rotation = Quaternion.Euler(0f, 0f, shown.Rotation);
            _flipX = shown.FlipX;
            _spriteId = shown.SpriteId; // the frame from the same moment, so animation matches movement
            ApplyFrame();

            // ETG draws by z = y - HeightOffGround (tilted world). Left at z = 0 the sprite sits far
            // off the level's depth range and is never drawn, so re-derive it after every move.
            if (_sprite != null) _sprite.UpdateZDepth();
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
            _sprite.SetSprite(source.Collection, source.spriteId);
            _sprite.HeightOffGround = source.HeightOffGround;
            _sprite.SortingOrder = source.SortingOrder;
            _sprite.scale = source.scale;
            _sprite.color = source.color;
            _sprite.IsPerpendicular = source.IsPerpendicular;
            _sprite.depthUsesTrimmedBounds = source.depthUsesTrimmedBounds;
            ApplyFrame();
            _sprite.UpdateZDepth();

            Debug.LogInfo($"[RemotePlayer] {name} sprite created: character={(_characterId == UnknownCharacter ? "unknown (local copy)" : ((PlayableCharacters)_characterId).ToString())}, " +
                          $"layer={LayerMask.LayerToName(spriteObject.layer)}, pos={_spriteTransform.position}");
        }

        private void DestroySprite()
        {
            if (_spriteTransform != null) Destroy(_spriteTransform.gameObject);
            _sprite = null;
            _spriteTransform = null;
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
