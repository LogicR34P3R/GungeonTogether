using UnityEngine;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;
using Object = UnityEngine.Object;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Shared shop stock (step 3d). Each side has its own shops - seeded runs make the stock match -
    /// and buying works locally as normal: ShopItemController.Interact checks and spends the shared
    /// money/keys pool (3b syncs the spend) and gives the item straight to the buyer. The only thing
    /// to sync is that the slot is now sold: Harmony postfixes (GungeonTogether.Patches.ShopPatches)
    /// report a purchase or theft, and the other side marks its matching slot sold with the game's
    /// own ForceOutOfStock - no item, no charge. Optimistic like pickups: two players buying the same
    /// slot at the same instant both get it.
    ///
    /// Foyer meta-currency shops are skipped: they spend each player's own saved currency.
    /// </summary>
    public class ShopReplicator : MonoSingleton<ShopReplicator>
    {
        private const float MatchRadius = 1f;

        /// <summary>Called by the Interact/ForceSteal postfixes once a slot has actually been sold.</summary>
        public static void OnSold(ShopItemController shopItem)
        {
            if (!NetworkSession.Instance.IsConnected || shopItem == null || shopItem.item == null) return;
            if (shopItem.CurrencyType == ShopItemController.ShopCurrencyType.META_CURRENCY) return;
            if (GameManager.Instance == null || GameManager.Instance.IsFoyer) return;

            var packet = new ShopItemSoldPacket
            {
                Position = shopItem.transform.position,
                PickupId = shopItem.item.PickupObjectId
            };
            if (NetworkSession.Instance.IsHost) NetworkSession.Instance.Broadcast(packet, reliable: true);
            else NetworkSession.Instance.SendToHost(packet, reliable: true);

            Debug.Log($"[ShopReplicator] Sold shop item {packet.PickupId} at {packet.Position}.");
        }

        public void HandleSold(ulong senderId, ShopItemSoldPacket packet)
        {
            ShopItemController slot = FindSlot(packet.Position, packet.PickupId);
            if (slot != null && !slot.Acquired)
            {
                slot.ForceOutOfStock();
            }
            else if (slot == null)
            {
                Debug.Log($"[ShopReplicator] No matching shop slot for item {packet.PickupId} at {packet.Position} (different stock?) - left as is.");
            }

            if (NetworkSession.Instance.IsHost)
            {
                NetworkSession.Instance.Broadcast(packet, excludeId: senderId, reliable: true);
            }
        }

        /// <summary>Rare event, so a scene search is fine.</summary>
        private static ShopItemController FindSlot(Vector2 position, int pickupId)
        {
            ShopItemController best = null;
            float bestDist = MatchRadius;
            foreach (ShopItemController candidate in Object.FindObjectsOfType<ShopItemController>())
            {
                if (candidate.item == null || candidate.item.PickupObjectId != pickupId) continue;
                float dist = Vector2.Distance(candidate.transform.position, position);
                if (dist <= bestDist)
                {
                    best = candidate;
                    bestDist = dist;
                }
            }
            return best;
        }
    }
}
