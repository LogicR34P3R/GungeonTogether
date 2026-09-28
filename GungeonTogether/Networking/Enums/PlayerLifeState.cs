namespace GungeonTogether.Networking.Enums
{
    /// <summary>A player's life as the other players see it. See PlayerLifeReplicator.</summary>
    public enum PlayerLifeState : byte
    {
        Alive = 0,
        Ghost = 1,  // died while a partner was alive: spectating as a co-op ghost
        Dead = 2    // game over: nobody was left alive
    }
}
