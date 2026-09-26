using UnityEngine;

namespace GungeonTogether.Networking.Entities
{
    /// <summary>
    /// Marks a client-side AIActor as a puppet of a host enemy (spawned by EnemyReplicator), so the
    /// client's native-enemy sweep never removes it. Carries no behaviour of its own.
    /// </summary>
    public class NetworkPuppet : MonoBehaviour
    {
    }
}
