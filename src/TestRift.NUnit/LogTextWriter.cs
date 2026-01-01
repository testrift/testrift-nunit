using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TestRift.NUnit
{
    /// <summary>
    /// Enhanced TextWriter that captures console output and sends it via WebSocket
    /// </summary>
    public class LogTextWriter : TextWriter
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        private readonly string _testCaseId;
        private readonly WebSocketHelper _webSocketHelper;
        private readonly StringBuilder _buffer = new StringBuilder();
        private readonly List<LogEntry> _pendingMessages = new List<LogEntry>();
        private DateTime _lineStartTime;

        public LogTextWriter(string filePath, string testCaseId = null, WebSocketHelper webSocketHelper = null)
        {
            _filePath = filePath;
            _testCaseId = testCaseId;
            _webSocketHelper = webSocketHelper;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_lock)
            {
                File.AppendAllText(_filePath, value.ToString());

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

            lock (_lock)
            {
                File.AppendAllText(_filePath, value);

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
            lock (_lock)
            {
                File.AppendAllText(_filePath, value + Environment.NewLine);

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
            lock (_lock)
            {
                FlushBuffer();
            }
        }

        private void FlushBuffer()
        {
            if (_buffer.Length > 0)
            {
                var message = _buffer.ToString().TrimEnd('\r', '\n');

                if (!string.IsNullOrEmpty(message))
                {
                    var phase = TeardownMonitor.OnActivity(
                        _testCaseId,
                        _webSocketHelper,
                        activity: "LogTextWriter.FlushBuffer",
                        aboutToSendLog: true,
                        timestamp: _lineStartTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));

                    // Create LogEntry with timestamp when message was first written
                    var logEntry = new LogEntry
                    {
                        timestamp = _lineStartTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        message = message,
                        component = Environment.MachineName,
                        channel = "console",
                        dir = null, // Console messages don't have direction
                        testCaseId = _testCaseId,
                        phase = phase
                    };

                    _pendingMessages.Add(logEntry);
                }
                _buffer.Clear();
            }

            // Send all pending messages
            if (_pendingMessages.Count > 0)
            {
                SendPendingMessages();
            }
        }

        private void SendPendingMessages()
        {
            if (_webSocketHelper != null && _pendingMessages.Count > 0)
            {
                // Send all pending messages at once using the new API
                foreach (var logEntry in _pendingMessages)
                {
                    _webSocketHelper.QueueLogMessage(
                        logEntry.message,
                        logEntry.component,
                        logEntry.channel,
                        logEntry.dir,
                        logEntry.testCaseId,
                        logEntry.timestamp,
                        logEntry.phase);
                }
                _pendingMessages.Clear();
            }
        }


        public new void Dispose()
        {
            lock (_lock)
            {
                FlushBuffer(); // Flush any remaining content
            }
        }
    }

    public class LogEntry
    {
        public string timestamp { get; set; }
        public string message { get; set; }
        public string component { get; set; }
        public string channel { get; set; }
        public string dir { get; set; }
        public string testCaseId { get; set; }
        // Optional execution phase marker. When present, UI can use it for grouping (e.g. teardown).
        public string phase { get; set; }
    }
}