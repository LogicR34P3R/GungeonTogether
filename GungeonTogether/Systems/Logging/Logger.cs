using System.Collections.Generic;
using BepInEx.Logging;

namespace GungeonTogether.Systems.Logging
{
    public static class Logger
    {
        private static ManualLogSource _logSource;

        /// <summary>Messages below this level are dropped before reaching BepInEx.</summary>
        public static LogLevel MinLevel { get; set; } = LogLevel.Info;

        private class ThrottleState
        {
            public float NextAllowedTime;
            public int Suppressed;
        }

        private static readonly Dictionary<string, ThrottleState> _throttles = new Dictionary<string, ThrottleState>();

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

        /// <summary>
        /// For warnings on per-packet/per-frame paths: logs the first occurrence per key, then at
        /// most once per interval, noting how many were suppressed in between.
        /// </summary>
        public static void LogWarningThrottled(string key, object data, float intervalSeconds = 5f)
        {
            if (!IsEnabled(LogLevel.Warning)) return;

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (!_throttles.TryGetValue(key, out var state))
            {
                state = new ThrottleState();
                _throttles[key] = state;
            }

            if (now < state.NextAllowedTime)
            {
                state.Suppressed++;
                return;
            }

            string suffix = state.Suppressed > 0 ? $" ({state.Suppressed} more suppressed in the last {intervalSeconds:0}s)" : "";
            _logSource?.LogWarning($"{data}{suffix}");
            state.Suppressed = 0;
            state.NextAllowedTime = now + intervalSeconds;
        }
    }
}
