using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Host: broadcasts GameManager's real loading state on change. Client: remembers it, so
    /// WorldStateReplicator can hold off teleporting the local player mid-load.
    /// </summary>
    public class LoadingStateReplicator : MonoSingleton<LoadingStateReplicator>
    {
        private bool _wasLoading;

        public bool IsClientLoading { get; private set; }

        private void Update()
        {
            if (!NetworkSession.Instance.IsHost) return;
            if (GameManager.Instance == null) return;

            bool isLoading = GameManager.Instance.IsLoadingLevel;
            if (isLoading == _wasLoading) return;

            _wasLoading = isLoading;
            NetworkSession.Instance.Broadcast(new LoadingStatePacket { IsLoading = isLoading }, reliable: true);
            Debug.Log($"[LoadingStateReplicator] Host loading state changed: {isLoading}");
        }

        public void ApplyLoadingState(bool isLoading)
        {
            IsClientLoading = isLoading;
        }
    }
}
