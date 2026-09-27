using System.Collections.Generic;
using UnityEngine;

namespace GungeonTogether.Networking.Entities
{
    public class NetworkEntityManager
    {
        private static NetworkEntityManager _instance;
        public static NetworkEntityManager Instance => _instance ??= new NetworkEntityManager();

        private int _nextId = 1;
        private Dictionary<object, int> _entityIds = new Dictionary<object, int>(); // host side: enemy object -> id
        private Dictionary<int, object> _entitiesById = new Dictionary<int, object>(); // host side: id -> enemy object (applying client damage)
        private Dictionary<int, GameObject> _remoteEntities = new Dictionary<int, GameObject>(); // client side: id -> remote object

        /// <summary>Host side: same id every call for a given entity, assigning one on first use.</summary>
        public int GetOrAssignId(object entity)
        {
            if (_entityIds.TryGetValue(entity, out int id)) return id;
            id = _nextId++;
            _entityIds[entity] = id;
            _entitiesById[id] = entity;
            return id;
        }

        /// <summary>Host side: the id of an entity that's already synced - never assigns a new one.</summary>
        public bool TryGetId(object entity, out int id) => _entityIds.TryGetValue(entity, out id);

        /// <summary>Host side: the entity a client is referring to by id.</summary>
        public bool TryGetEntity(int id, out object entity) => _entitiesById.TryGetValue(id, out entity);

        public void AddRemote(int id, GameObject go) => _remoteEntities[id] = go;
        public GameObject GetRemote(int id) => _remoteEntities.TryGetValue(id, out var go) ? go : null;
        // go can already be Unity-destroyed here (e.g. the client killed its local copy first).
        public void RemoveRemote(int id) { if (_remoteEntities.TryGetValue(id, out var go) && go != null) Object.Destroy(go); _remoteEntities.Remove(id); }

        /// <summary>Resets the registry - call on both host and client when the room changes, so ids don't outlive the room they were assigned in.</summary>
        public void Clear()
        {
            foreach (var kv in _remoteEntities) if (kv.Value != null) Object.Destroy(kv.Value);
            _remoteEntities.Clear();
            _entityIds.Clear();
            _entitiesById.Clear();
            _nextId = 1;
        }
    }
}
