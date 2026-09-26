namespace GungeonTogether.Systems.Logging
{
    /// <summary>
    /// Minimum severity GungeonTogether will pass on to BepInEx. Ordered least to most severe.
    /// </summary>
    public enum LogLevel
    {
        /// <summary>Per-packet / per-frame noise: broadcasts, UI layout dumps.</summary>
        Trace,
        /// <summary>Handshake steps and other detail useful when debugging one subsystem.</summary>
        Debug,
        /// <summary>Lifecycle events: hosting, joining, lobby created, player spawned/removed.</summary>
        Info,
        Warning,
        Error
    }
}
