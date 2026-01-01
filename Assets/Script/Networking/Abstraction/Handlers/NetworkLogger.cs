using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace YARG.Networking.Abstraction.Handlers
{
    /// <summary>
    /// Centralized logging for networking code with conditional compilation.
    /// In release builds, verbose logging is stripped out entirely.
    /// </summary>
    public static class NetworkLogger
    {
        private const string LOG_PREFIX = "[LiteNet]";
        
        /// <summary>
        /// Log levels for filtering output.
        /// </summary>
        public enum LogLevel
        {
            Verbose = 0,  // Very detailed, for debugging
            Info = 1,     // Normal operations
            Warning = 2,  // Potential issues
            Error = 3     // Errors
        }
        
        /// <summary>
        /// Minimum log level to display. Can be changed at runtime.
        /// </summary>
        public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;
        
        /// <summary>
        /// Whether to include timestamps in log messages.
        /// </summary>
        public static bool IncludeTimestamp { get; set; } = false;
        
        /// <summary>
        /// Log a verbose message. Only included in DEBUG builds.
        /// Use for detailed debugging information.
        /// </summary>
        [Conditional("DEBUG")]
        public static void Verbose(string message)
        {
            if (MinimumLevel <= LogLevel.Verbose)
            {
                LogInternal(message, LogLevel.Verbose);
            }
        }
        
        /// <summary>
        /// Log a verbose message with formatting. Only included in DEBUG builds.
        /// </summary>
        [Conditional("DEBUG")]
        public static void Verbose(string format, params object[] args)
        {
            if (MinimumLevel <= LogLevel.Verbose)
            {
                LogInternal(string.Format(format, args), LogLevel.Verbose);
            }
        }
        
        /// <summary>
        /// Log an info message. Standard operational logging.
        /// </summary>
        public static void Info(string message)
        {
            if (MinimumLevel <= LogLevel.Info)
            {
                LogInternal(message, LogLevel.Info);
            }
        }
        
        /// <summary>
        /// Log an info message with formatting.
        /// </summary>
        public static void Info(string format, params object[] args)
        {
            if (MinimumLevel <= LogLevel.Info)
            {
                LogInternal(string.Format(format, args), LogLevel.Info);
            }
        }
        
        /// <summary>
        /// Log a warning message.
        /// </summary>
        public static void Warn(string message)
        {
            if (MinimumLevel <= LogLevel.Warning)
            {
                Debug.LogWarning(FormatMessage(message));
            }
        }
        
        /// <summary>
        /// Log a warning message with formatting.
        /// </summary>
        public static void Warn(string format, params object[] args)
        {
            if (MinimumLevel <= LogLevel.Warning)
            {
                Debug.LogWarning(FormatMessage(string.Format(format, args)));
            }
        }
        
        /// <summary>
        /// Log an error message.
        /// </summary>
        public static void Error(string message)
        {
            Debug.LogError(FormatMessage(message));
        }
        
        /// <summary>
        /// Log an error message with formatting.
        /// </summary>
        public static void Error(string format, params object[] args)
        {
            Debug.LogError(FormatMessage(string.Format(format, args)));
        }
        
        /// <summary>
        /// Log an exception.
        /// </summary>
        public static void Exception(Exception ex, string context = null)
        {
            if (string.IsNullOrEmpty(context))
            {
                Debug.LogError($"{LOG_PREFIX} Exception: {ex.Message}");
            }
            else
            {
                Debug.LogError($"{LOG_PREFIX} {context}: {ex.Message}");
            }
            Debug.LogException(ex);
        }
        
        private static void LogInternal(string message, LogLevel level)
        {
            Debug.Log(FormatMessage(message));
        }
        
        private static string FormatMessage(string message)
        {
            if (IncludeTimestamp)
            {
                return $"{LOG_PREFIX} [{DateTime.Now:HH:mm:ss.fff}] {message}";
            }
            return $"{LOG_PREFIX} {message}";
        }
        
        #region Context-Specific Helpers
        
        /// <summary>
        /// Log a server-side message.
        /// </summary>
        public static void Server(string message)
        {
            Info($"[Server] {message}");
        }
        
        /// <summary>
        /// Log a server-side message with formatting.
        /// </summary>
        public static void Server(string format, params object[] args)
        {
            Info($"[Server] {string.Format(format, args)}");
        }
        
        /// <summary>
        /// Log a client-side message.
        /// </summary>
        [Conditional("DEBUG")]
        public static void Client(string message)
        {
            Verbose($"[Client] {message}");
        }
        
        /// <summary>
        /// Log a client-side message with formatting.
        /// </summary>
        [Conditional("DEBUG")]
        public static void Client(string format, params object[] args)
        {
            Verbose($"[Client] {string.Format(format, args)}");
        }
        
        /// <summary>
        /// Log a packet-related message.
        /// </summary>
        [Conditional("DEBUG")]
        public static void Packet(string packetType, string details)
        {
            Verbose($"[Packet:{packetType}] {details}");
        }
        
        /// <summary>
        /// Log a packet-related message with formatting.
        /// </summary>
        [Conditional("DEBUG")]
        public static void Packet(string packetType, string format, params object[] args)
        {
            Verbose($"[Packet:{packetType}] {string.Format(format, args)}");
        }
        
        /// <summary>
        /// Log a connection event.
        /// </summary>
        public static void Connection(string message)
        {
            Info($"[Connection] {message}");
        }
        
        /// <summary>
        /// Log a connection event with formatting.
        /// </summary>
        public static void Connection(string format, params object[] args)
        {
            Info($"[Connection] {string.Format(format, args)}");
        }
        
        #endregion
    }
}
