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
                ChestReplicator.Instance.gameObject.SetActive(true);
                ShopReplicator.Instance.gameObject.SetActive(true);
                ProjectileReplicator.Instance.gameObject.SetActive(true);
                DamageReplicator.Instance.gameObject.SetActive(true);
                ScriptReplicator.Instance.gameObject.SetActive(true);
                PlayerShotReplicator.Instance.gameObject.SetActive(true);
                PlayerLifeReplicator.Instance.gameObject.SetActive(true);
                RoomObjectReplicator.Instance.gameObject.SetActive(true);
                GenerationReplicator.Instance.gameObject.SetActive(true);
                BindSyncOptions();

                ApplyHarmonyPatches();

                Logger.LogInfo("Gungeon Together ready.");
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Exception during initialization: {ex.GetType().Name}: {ex.Message}");
                Logger.LogError($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Runtime patches into game code (GungeonTogether.Patches) - only where the game offers no
        /// public hook. Applied one patch class at a time rather than with PatchAll, which stops at
        /// the first failure: a patch that no longer matches the game then only costs its own feature.
        /// </summary>
        private void ApplyHarmonyPatches()
        {
            var harmony = new HarmonyLib.Harmony("com.llamerrr.gungeontogether");
            int applied = 0, failed = 0;
            foreach (System.Type type in HarmonyLib.AccessTools.GetTypesFromAssembly(typeof(GungeonTogetherMod).Assembly))
            {
                if (type.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false).Length == 0) continue;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    applied++;
                }
                catch (System.Exception ex)
                {
                    failed++;
                    Logger.LogError($"Harmony patch {type.Name} failed - its feature will not work: {ex.Message}");
                }
            }
            Logger.LogInfo($"Harmony: {applied} patch(es) applied, {failed} failed.");
        }

        private void BindSyncOptions()
        {
            var bossScriptReplay = Config.Bind("Sync", "BossScriptReplay", true,
                "Host only. Replay enemy and boss attack scripts on clients so their bullet patterns match (the name " +
                "is historical). Turn off if patterns look wrong on the client - bullets then fall back to straight-line copies.");

            ScriptReplicator.Enabled = bossScriptReplay.Value;
            bossScriptReplay.SettingChanged += (_, __) => ScriptReplicator.Enabled = bossScriptReplay.Value;
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

        private void OnGUI()
        {
            try
            {
                UIManager.OnGUI();
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Error in OnGUI: {ex.Message}");
            }
        }
    }
}
