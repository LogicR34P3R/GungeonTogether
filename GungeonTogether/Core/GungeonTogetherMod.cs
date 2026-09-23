using BepInEx;
using UnityEngine;
using GungeonTogether.Networking.Session;
using GungeonTogether.Networking.Replication;
using GungeonTogether.Systems.Logging;
using GungeonTogether.UI;

namespace GungeonTogether.Core
{
    [BepInPlugin("com.llamerrr.gungeontogether", "Gungeon Together", "1.0.0")]
    [BepInDependency("etgmodding.etg.mtgapi")]
    public class GungeonTogetherMod : BaseUnityPlugin
    {
        public static GungeonTogetherMod Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            
            try
            {
                // Initialise Logging
                GungeonTogether.Systems.Logging.Logger.Initialise(base.Logger);
                Logger.LogInfo("Gungeon Together starting...");

                // Initialise Networking
                NetworkSession.Instance.Initialise();

                // Initialise UI
                UIManager.Initialise();
                
                //initialise room/world/player/loading sync
                EnemyReplicator.Instance.gameObject.SetActive(true);
                WorldStateReplicator.Instance.gameObject.SetActive(true);
                PlayerReplicator.Instance.gameObject.SetActive(true);
                LoadingStateReplicator.Instance.gameObject.SetActive(true);

                Logger.LogInfo("Gungeon Together ready.");
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Exception during initialization: {ex.GetType().Name}: {ex.Message}");
                Logger.LogError($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        private void Update()
        {
            try
            {
                NetworkSession.Instance.Update();
                UIManager.Update();
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Error in Update: {ex.Message}");
            }
        }
    }
}
