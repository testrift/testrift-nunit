using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace TestRift.NUnit
{
    /// <summary>
    /// Direction for log messages
    /// </summary>
    public enum Direction
    {
        /// <summary>
        /// Transmitted - message sent from local side (host → component)
        /// </summary>
        Tx,

        /// <summary>
        /// Received - message received by local side (component → host)
        /// </summary>
        Rx
    }

    /// <summary>
    /// Helper class for sending log messages with component, channel, and direction.
    /// Initialized with a component (and optional channel) to avoid repetition.
    /// Automatically retrieves WebSocketHelper from TestContextWrapper.
    /// </summary>
    public class TRLog
    {
        private readonly string _component;
        private readonly string _channel;

        /// <summary>
        /// Creates a TRLog instance with a component and optional channel.
        /// </summary>
        /// <param name="component">The component name (required)</param>
        /// <param name="channel">The channel name (optional)</param>
        public TRLog(string component, string channel = null)
        {
            _component = component ?? throw new ArgumentNullException(nameof(component));
            _channel = channel;
        }

        /// <summary>
        /// Static method to send a log message with component, channel, direction, and message.
        /// </summary>
        /// <param name="component">The component name</param>
        /// <param name="message">The log message text</param>
        /// <param name="channel">Optional channel name</param>
        /// <param name="dir">Optional direction</param>
        public static void Log(string component, string message, string channel = null, Direction? dir = null)
        {
            if (string.IsNullOrEmpty(message)) return;

            var webSocketHelper = TestContextWrapper.GetWebSocketHelper();
            if (webSocketHelper == null) return;

            var nunitTestId = GetCurrentTestCaseId();
            var dirString = dir.HasValue ? (dir.Value == Direction.Tx ? "tx" : "rx") : null;
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var phase = TeardownMonitor.OnActivity(
                nunitTestId,
                webSocketHelper,
                activity: "TRLog.Log",
                aboutToSendLog: true,
                timestamp: timestamp);

            webSocketHelper.QueueLogMessage(message, component, channel, dirString, nunitTestId, timestamp, phase);
        }

        /// <summary>
        /// Sends a log message using the instance's component and channel.
        /// </summary>
        /// <param name="message">The log message text</param>
        /// <param name="dir">Optional direction</param>
        public void Log(string message, Direction? dir = null)
        {
            Log(_component, message, _channel, dir);
        }

        /// <summary>
        /// Sends a log message with direction Tx (transmitted).
        /// </summary>
        /// <param name="message">The log message text</param>
        public void LogTx(string message)
        {
            Log(message, Direction.Tx);
        }

        /// <summary>
        /// Sends a log message with direction Rx (received).
        /// </summary>
        /// <param name="message">The log message text</param>
        public void LogRx(string message)
        {
            Log(message, Direction.Rx);
        }

        private static string GetCurrentTestCaseId()
        {
            try
            {
                // Use NUnit test.ID for reliable lookup even when tests run concurrently (TestAdapter uses ID, not Id)
                return TestContext.CurrentContext?.Test?.ID ?? null;
            }
            catch
            {
                return null;
            }
        }
    }
}

