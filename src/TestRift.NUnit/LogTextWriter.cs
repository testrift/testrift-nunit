using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace TestRift.NUnit
{
    /// <summary>
    /// TextWriter that captures console output and routes it to WebSocketHelper
    /// </summary>
    public class LogTextWriter : TextWriter
    {
        private readonly WebSocketHelper _webSocketHelper;
        private readonly StringBuilder _buffer = new StringBuilder();
        private DateTime _lineStartTime;

        public LogTextWriter(WebSocketHelper webSocketHelper = null)
        {
            _webSocketHelper = webSocketHelper;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_buffer)
            {
                // If starting a new line, capture timestamp
                if (_buffer.Length == 0)
                {
                    _lineStartTime = DateTime.UtcNow;
                }

                _buffer.Append(value);

                // If it's a newline, flush the buffer
                if (value == '\n')
                {
                    FlushBuffer();
                }
            }
        }

        public override void Write(string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            lock (_buffer)
            {
                // If starting a new line, capture timestamp
                if (_buffer.Length == 0)
                {
                    _lineStartTime = DateTime.UtcNow;
                }

                _buffer.Append(value);

                // If the string contains newlines, flush complete lines
                if (value.Contains("\n"))
                {
                    FlushBuffer();
                }
            }
        }

        public override void WriteLine(string value)
        {
            lock (_buffer)
            {
                // If starting a new line, capture timestamp
                if (_buffer.Length == 0)
                {
                    _lineStartTime = DateTime.UtcNow;
                }

                _buffer.AppendLine(value);
                FlushBuffer(); // Always flush on WriteLine
            }
        }

        public override void Flush()
        {
            lock (_buffer)
            {
                FlushBuffer();
            }
        }

        private void FlushBuffer()
        {
            if (_buffer.Length == 0 || _webSocketHelper == null)
            {
                return;
            }

            var message = _buffer.ToString().TrimEnd('\r', '\n');
            _buffer.Clear();

            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            // Get current test ID from TestContext
            string nunitTestId = GetCurrentTestId();
            if (string.IsNullOrEmpty(nunitTestId))
            {
                return;
            }

            // Get phase from TeardownMonitor
            string phase = TeardownMonitor.OnActivity(
                nunitTestId,
                _webSocketHelper,
                activity: "LogTextWriter.FlushBuffer",
                aboutToSendLog: true,
                timestamp: _lineStartTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));

            // Queue the message - WebSocketHelper will handle buffering per test case
            _webSocketHelper.QueueLogMessage(
                message,
                Environment.MachineName,
                "console",
                null, // No direction for console messages
                nunitTestId,
                _lineStartTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                phase);
        }

        /// <summary>
        /// Gets the current test ID from TestContext
        /// </summary>
        private string GetCurrentTestId()
        {
            try
            {
                return TestContext.CurrentContext?.Test?.ID;
            }
            catch
            {
                return null;
            }
        }
    }
}
