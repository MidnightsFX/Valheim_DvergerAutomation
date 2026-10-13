using BepInEx.Logging;
using System;


namespace DvergerAutomation {
    internal class Logger {
        public static LogLevel Level = LogLevel.Info;

        /// <summary>SettingChanged handler for the debug-mode entry.</summary>
        public static void EnableDebugLogging(object sender, EventArgs e) {
            SetDebugLogging(ValConfig.EnableDebugMode.Value);
        }

        public static void SetDebugLogging(bool state) {
            Level = state ? LogLevel.Debug : LogLevel.Info;
        }

        public static void LogDebug(string message) {
            if (Level >= LogLevel.Debug) {
                DvergerAutomation.Log.LogInfo("[DEBUG]" + message);
            }
        }
        public static void LogInfo(string message) {
            if (Level >= LogLevel.Info) {
                DvergerAutomation.Log.LogInfo(message);
            }
        }

        public static void LogWarning(string message) {
            if (Level >= LogLevel.Warning) {
                DvergerAutomation.Log.LogWarning(message);
            }
        }

        public static void LogError(string message) {
            if (Level >= LogLevel.Error) {
                DvergerAutomation.Log.LogError(message);
            }
        }
    }
}
