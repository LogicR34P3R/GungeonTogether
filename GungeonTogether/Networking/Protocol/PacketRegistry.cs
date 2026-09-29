using System;
using System.Collections.Generic;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Interfaces;
using GungeonTogether.Networking.Enums;

namespace GungeonTogether.Networking.Protocol
{
    public static class PacketRegistry
    {
        private static readonly Dictionary<PacketType, Type> _packetTypes = new Dictionary<PacketType, Type>
        {
            { PacketType.PlayerPosition, typeof(PlayerPositionPacket) },
            { PacketType.ConnectionRequest, typeof(ConnectionRequestPacket) },
            { PacketType.ConnectionAccepted, typeof(ConnectionAcceptedPacket) },
            { PacketType.Disconnect, typeof(DisconnectPacket) },
            { PacketType.ConnectionRejected, typeof(ConnectionRejectedPacket) },
            { PacketType.PlayerJoin, typeof(PlayerJoinPacket) },
            { PacketType.PlayerLeave, typeof(PlayerLeavePacket) },
            { PacketType.RoomChange, typeof(RoomChangePacket) },
            { PacketType.EnemySpawn, typeof(EnemySpawnPacket) },
            { PacketType.EnemyState, typeof(EnemyStatePacket) },
            { PacketType.EnemyDeath, typeof(EnemyDeathPacket) },
            { PacketType.WorldState, typeof(WorldStatePacket) },
            { PacketType.PlayerState, typeof(PlayerStatePacket) },
            { PacketType.LoadingState, typeof(LoadingStatePacket) },
            { PacketType.Heartbeat, typeof(HeartbeatPacket) },
            { PacketType.RunSeed, typeof(RunSeedPacket) },
            { PacketType.LayoutHash, typeof(LayoutHashPacket) },
            { PacketType.LevelTransition, typeof(LevelTransitionPacket) },
            { PacketType.RoomCleared, typeof(RoomClearedPacket) },
            { PacketType.RoomSealState, typeof(RoomSealStatePacket) },
            { PacketType.LootSpawn, typeof(LootSpawnPacket) },
            { PacketType.LootTaken, typeof(LootTakenPacket) },
            { PacketType.ConsumablesState, typeof(ConsumablesStatePacket) },
            { PacketType.ConsumablesDelta, typeof(ConsumablesDeltaPacket) },
            { PacketType.ChestSpawn, typeof(ChestSpawnPacket) },
            { PacketType.ChestInteract, typeof(ChestInteractPacket) },
            { PacketType.ChestState, typeof(ChestStatePacket) },
            { PacketType.ShopItemSold, typeof(ShopItemSoldPacket) },
            { PacketType.EnemyProjectile, typeof(EnemyProjectilePacket) },
            { PacketType.EnemyDamage, typeof(EnemyDamagePacket) },
            { PacketType.FloorCleared, typeof(FloorClearedPacket) },
            { PacketType.BossScriptStart, typeof(BossScriptStartPacket) },
            { PacketType.BossScriptStop, typeof(BossScriptStopPacket) },
            { PacketType.ClientEnteredRoom, typeof(ClientEnteredRoomPacket) },
            { PacketType.PlayerProjectile, typeof(PlayerProjectilePacket) },
            { PacketType.PlayerLife, typeof(PlayerLifePacket) },
            { PacketType.RoomObject, typeof(RoomObjectPacket) },
            { PacketType.GenerationDecisions, typeof(GenerationDecisionsPacket) },
            { PacketType.Explosion, typeof(ExplosionPacket) },
            { PacketType.EnemyHit, typeof(EnemyHitPacket) },
            { PacketType.Goop, typeof(GoopPacket) },
            { PacketType.Beam, typeof(BeamPacket) },
        };

        public static INetworkPacket Create(PacketType type)
        {
            if (_packetTypes.TryGetValue(type, out Type classType))
            {
                return (INetworkPacket)Activator.CreateInstance(classType);
            }
            return null;
        }
    }
}
