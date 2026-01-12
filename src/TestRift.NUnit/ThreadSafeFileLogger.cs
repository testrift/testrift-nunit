using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;

namespace TestRift.NUnit
{
    public static class ThreadSafeFileLogger
    {
        private static readonly object _lock = new object();
        private static readonly string _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testrift_nunit.log");

        public static void Log(string message)
        {
            lock (_lock)
            {
                try
                {
                    File.AppendAllText(_logFilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
                }
                catch (Exception)
                {
                    // Silent fail - don't log to console as it creates infinite loop
                    // Could write to a different log file if needed
                }
            }
        }

        public static void LogRunStarted()
        {
            Log("=== RUN STARTED (from SetUpFixture) ===");
        }

        public static void LogRunFinished()
        {
            Log("=== RUN FINISHED (from SetUpFixture) ===");
        }

        public static void LogWebSocketConnected(string uri)
        {
            Log($"WebSocket connected to {uri}");
        }

        public static void LogWebSocketConnectionFailed(string error)
        {
            Log($"WebSocket connection failed: {error}");
        }

        public static void LogWebSocketNotOpen(string state)
        {
            Log($"WebSocket not open, state: {state}");
        }

        public static void LogMessageSent(string json)
        {
            Log($"Sent message: {json}");
        }

        public static void LogSendFailed(string error)
        {
            Log($"Send failed: {error}");
        }

        public static void LogSendStarting(long messageId, string messageType, string stateSnapshot)
        {
            Log($"Send[{messageId}] starting (type={messageType}, socket={stateSnapshot})");
        }

        public static void LogSendCompleted(long messageId, string messageType, int byteCount)
        {
            Log($"Send[{messageId}] completed (type={messageType}, bytes={byteCount})");
        }

        public static void LogWebSocketStateDetails(string context, WebSocketState? state, WebSocketCloseStatus? closeStatus, string closeDescription)
        {
            Log($"WebSocket state ({context}): state={state?.ToString() ?? "null"}, closeStatus={closeStatus?.ToString() ?? "null"}, closeDescription={closeDescription ?? "null"}");
        }

        public static void LogWebSocketException(string context, Exception ex)
        {
            Log($"WebSocket exception ({context}): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        public static void LogBeforeTest(string testName, bool isSuite, string parent)
        {
            Log($"BeforeTest called: IsSuite={isSuite}, Parent={parent ?? "null"}, FullName={testName}");
        }

        public static void LogStartTest(string testName)
        {
            Log($"[START TEST] {testName}");
        }

        public static void LogEndTest(string testName, string outcome)
        {
            Log($"[END TEST] {testName} => {outcome}");
        }

        public static void LogRunHooksConstructor()
        {
            Log("=== RunHooks CONSTRUCTOR CALLED ===");
        }

        public static void LogRunHooksStarted()
        {
            Log("=== SetUpFixture RunStarted called ===");
        }

        public static void LogRunHooksFinished()
        {
            Log("=== SetUpFixture RunFinished called ===");
        }
    }
}
