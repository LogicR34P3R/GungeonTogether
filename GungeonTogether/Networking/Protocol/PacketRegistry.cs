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
