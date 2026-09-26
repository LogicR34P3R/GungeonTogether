namespace GungeonTogether.Systems.Logging
{
    public static class Debug
    {
        public static void LogTrace(object message) => Logger.LogTrace(message);
        public static void Log(object message) => Logger.LogDebug(message);
        public static void LogInfo(object message) => Logger.LogInfo(message);
        public static void LogWarning(object message) => Logger.LogWarning(message);
        public static void LogWarningThrottled(string key, object message, float intervalSeconds = 5f) => Logger.LogWarningThrottled(key, message, intervalSeconds);
        public static void LogError(object message) => Logger.LogError(message);
    }
}
