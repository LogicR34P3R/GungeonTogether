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
                BindLogLevel();
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
                DungeonSeedReplicator.Instance.gameObject.SetActive(true);
                LootReplicator.Instance.gameObject.SetActive(true);
                ConsumablesReplicator.Instance.gameObject.SetActive(true);

                // Runtime patches into game code (GungeonTogether.Patches) - only where the game
                // offers no public hook, e.g. observing LootEngine spawns for loot sync. Isolated so
                // a patch that no longer matches the game only costs that feature, not the whole mod.
                try
                {
                    new HarmonyLib.Harmony("com.llamerrr.gungeontogether").PatchAll(typeof(GungeonTogetherMod).Assembly);
                }
                catch (System.Exception ex)
                {
                    Logger.LogError($"Harmony patching failed - loot sync will not work: {ex}");
                }

                Logger.LogInfo("Gungeon Together ready.");
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Exception during initialization: {ex.GetType().Name}: {ex.Message}");
                Logger.LogError($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        private void BindLogLevel()
        {
            var logLevel = Config.Bind("Logging", "LogLevel", LogLevel.Info,
                "Minimum GungeonTogether log level. Trace logs every broadcast packet; Debug adds handshake detail. " +
                "Trace/Debug messages are sent to BepInEx as Debug, so BepInEx's own [Logging.Console]/[Logging.Disk] " +
                "LogLevels must include Debug to see them.");

            GungeonTogether.Systems.Logging.Logger.MinLevel = logLevel.Value;
            logLevel.SettingChanged += (_, __) => GungeonTogether.Systems.Logging.Logger.MinLevel = logLevel.Value;
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
