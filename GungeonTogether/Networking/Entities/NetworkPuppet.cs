using UnityEngine;

namespace GungeonTogether.Networking.Entities
{
    /// <summary>
    /// Marks a client-side AIActor as a puppet of a host enemy (spawned by EnemyReplicator), so the
    /// client's native-enemy sweep never removes it, and records which host enemy it stands for so
    /// hits on it can be forwarded (DamageReplicator).
    /// </summary>
    public class NetworkPuppet : MonoBehaviour
    {
        public int EnemyId;
    }
}
