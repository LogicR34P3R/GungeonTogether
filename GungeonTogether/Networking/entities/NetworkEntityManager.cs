using System.Collections.Generic;
using UnityEngine;

public class NetworkEntityManager
{
    private static NetworkEntityManager _instance;
    public static NetworkEntityManager Instance => _instance ??= new NetworkEntityManager();

    private int _nextId = 1;
    private Dictionary<int, object> _entities = new Dictionary<int, object>(); // host side: id -> enemy object
    private Dictionary<object, int> _entityIds = new Dictionary<object, int>(); // host side: enemy object -> id (reverse lookup)
    private Dictionary<int, GameObject> _remoteEntities = new Dictionary<int, GameObject>(); // client side: id -> remote object

    public int AssignId(object entity) { int id = _nextId++; _entities[id] = entity; return id; }

    /// <summary>Host side: same id every call for a given entity, assigning one on first use.</summary>
    public int GetOrAssignId(object entity)
    {
        if (_entityIds.TryGetValue(entity, out int id)) return id;
        id = AssignId(entity);
        _entityIds[entity] = id;
        return id;
    }

    public object GetEntity(int id) => _entities.TryGetValue(id, out var e) ? e : null;
    public void AddRemote(int id, GameObject go) => _remoteEntities[id] = go;
    public GameObject GetRemote(int id) => _remoteEntities.TryGetValue(id, out var go) ? go : null;
    public void RemoveRemote(int id) { if (_remoteEntities.TryGetValue(id, out var go)) Object.Destroy(go); _remoteEntities.Remove(id); }

    /// <summary>Resets the registry - call on both host and client when the room changes, so ids don't outlive the room they were assigned in.</summary>
    public void Clear()
    {
        foreach (var kv in _remoteEntities) Object.Destroy(kv.Value);
        _remoteEntities.Clear();
        _entities.Clear();
        _entityIds.Clear();
        _nextId = 1;
    }
}