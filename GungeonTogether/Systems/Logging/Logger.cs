using BepInEx.Logging;

namespace GungeonTogether.Systems.Logging
{
    public static class Logger
    {
        private static ManualLogSource _logSource;

        /// <summary>Messages below this level are dropped before reaching BepInEx.</summary>
        public static LogLevel MinLevel { get; set; } = LogLevel.Info;

        public static void Initialise(ManualLogSource logSource)
        {
            _logSource = logSource;
        }

        public static bool IsEnabled(LogLevel level) => level >= MinLevel;

        // BepInEx has no Trace level, so Trace goes out as Debug. Note BepInEx's own
        // [Logging.Console]/[Logging.Disk] LogLevels filter still applies after this gate.
        public static void LogTrace(object data) { if (IsEnabled(LogLevel.Trace)) _logSource?.LogDebug(data); }
        public static void LogDebug(object data) { if (IsEnabled(LogLevel.Debug)) _logSource?.LogDebug(data); }
        public static void LogInfo(object data) { if (IsEnabled(LogLevel.Info)) _logSource?.LogInfo(data); }
        public static void LogWarning(object data) { if (IsEnabled(LogLevel.Warning)) _logSource?.LogWarning(data); }
        public static void LogError(object data) { if (IsEnabled(LogLevel.Error)) _logSource?.LogError(data); }
    }
}
