using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TestRift.NUnit
{
    /// <summary>
    /// Helper class for managing WebSocket connections and high-performance event batching.
    /// Uses a unified event queue to batch test case starts, finishes, logs, and exceptions
    /// into single WebSocket frames for optimal throughput.
    /// </summary>
    public class WebSocketHelper : IDisposable
    {
        private const int BatchIntervalMs = 250;        // Send batches every 250ms
        private const int MaxBatchSizeBytes = 131072;   // Flush when buffered data exceeds 128KB
        private const int MaxEventsPerBatch = 500;      // Maximum events per batch message
        private const int HeartbeatIntervalMs = 10000;  // Send heartbeat every 10 seconds if no activity

        private readonly string _serverBaseUrl;
        private string _runId;  // Set by server response after run_started
        private ClientWebSocket _webSocket;

        // Unified event queue for all event types (TC start, TC finish, logs, exceptions)
        private readonly ConcurrentQueue<BatchEvent> _eventQueue = new ConcurrentQueue<BatchEvent>();
        private readonly Timer _batchTimer;
        private readonly Timer _heartbeatTimer;
        private bool _disposed = false;
        private readonly SemaphoreSlim _sendSemaphore = new SemaphoreSlim(1, 1);
        private readonly HttpClient _httpClient;
        private CancellationTokenSource _receiveLoopCts;
        private Task _receiveLoopTask;
        private long _messageSequence;
        private DateTime _lastSendTime = DateTime.UtcNow;

        // Tracks test cases that have reported unexpected/unhandled errors (is_error = true)
        private readonly ConcurrentDictionary<string, bool> _testCasesWithErrors = new ConcurrentDictionary<string, bool>();
        // Maps tc_full_name to tc_id
        private readonly ConcurrentDictionary<string, string> _testCaseIds = new ConcurrentDictionary<string, string>();
        // Maps NUnit test.Id to tc_id (for concurrent test execution)
        private readonly ConcurrentDictionary<string, string> _testIdToTcId = new ConcurrentDictionary<string, string>();

        // Estimated current batch size for triggering early flush
        private long _estimatedBatchSize = 0;

        // JSON serializer options
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public WebSocketHelper(string serverBaseUrl = null)
        {
            if (!string.IsNullOrWhiteSpace(serverBaseUrl))
            {
                _serverBaseUrl = serverBaseUrl;
            }
            else
            {
                try
                {
                    var cfg = ConfigManager.Get();
                    _serverBaseUrl = !string.IsNullOrWhiteSpace(cfg.ServerUrl) ? cfg.ServerUrl : "http://localhost:8080";
                }
                catch
                {
                    _serverBaseUrl = "http://localhost:8080";
                }
            }
            _runId = null;

            // Initialize batch timer for regular flushing
            _batchTimer = new Timer(OnBatchTimerTick, null, BatchIntervalMs, BatchIntervalMs);

            // Initialize heartbeat timer to keep connection alive during idle periods
            _heartbeatTimer = new Timer(OnHeartbeatTimerTick, null, HeartbeatIntervalMs, HeartbeatIntervalMs);

            _httpClient = new HttpClient();
        }

        public async Task ConnectAsync()
        {
            try
            {
                _webSocket = new ClientWebSocket();
                _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                var uri = new Uri(_serverBaseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws/nunit");
                await _webSocket.ConnectAsync(uri, CancellationToken.None);

                ThreadSafeFileLogger.LogWebSocketConnected(uri.ToString());
                ThreadSafeFileLogger.LogWebSocketStateDetails("Post-connect", _webSocket.State, _webSocket.CloseStatus, _webSocket.CloseStatusDescription);
            }
            catch (Exception ex)
            {
                ThreadSafeFileLogger.LogWebSocketException("ConnectAsync", ex);
                ThreadSafeFileLogger.LogWebSocketConnectionFailed(ex.Message);
            }
        }

        public async Task SendRunStarted()
        {
            var runId = GetRunId();
            var runName = GetRunName();
            var startTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var userMetadata = GetUserMetadata();
            var groupData = GetGroupData();

            var dataDict = new Dictionary<string, object>
            {
                { "type", "run_started" },
                { "run_name", runName },
                { "start_time", startTime },
                { "user_metadata", userMetadata },
                { "group", groupData }
            };
            if (!string.IsNullOrEmpty(runId))
            {
                dataDict["run_id"] = runId;
            }

            await SendWebSocketMessage(dataDict);
            await WaitForRunStartedResponse();
            StartPassiveReceiveLoop();
        }

        private string GetRunName()
        {
            try
            {
                var config = ConfigManager.Get();
                return config.RunName;
            }
            catch
            {
                return null;
            }
        }

        private string GetRunId()
        {
            try
            {
                var config = ConfigManager.Get();
                return config.RunId;
            }
            catch
            {
                return null;
            }
        }

        private async Task WaitForRunStartedResponse()
        {
            try
            {
                if (_webSocket?.State != WebSocketState.Open)
                {
                    var stateSnapshot = DescribeSocketState();
                    ThreadSafeFileLogger.LogWebSocketNotOpen(stateSnapshot);
                    throw new InvalidOperationException($"WebSocket connection is not open (state: {stateSnapshot})");
                }

                var buffer = new byte[4096];
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("type", out var typeProp) &&
                        typeProp.GetString() == "run_started_response")
                    {
                        if (root.TryGetProperty("error", out var errorProp))
                        {
                            var errorMessage = errorProp.GetString();
                            ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Run start failed: {errorMessage}");
                            throw new InvalidOperationException($"Run start failed: {errorMessage}");
                        }

                        if (root.TryGetProperty("run_id", out var runIdProp))
                        {
                            _runId = runIdProp.GetString();
                            ThreadSafeFileLogger.LogMessageSent($"Received run_id from server: {_runId}");
                        }

                        WriteUrlFilesFromResponse(root);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                ThreadSafeFileLogger.LogWebSocketConnectionFailed("Timeout waiting for run_started_response");
                throw new InvalidOperationException("Timeout waiting for run_started_response from server");
            }
        }

        private void StartPassiveReceiveLoop()
        {
            if (_receiveLoopTask != null && !_receiveLoopTask.IsCompleted)
                return;

            _receiveLoopCts?.Cancel();
            _receiveLoopCts?.Dispose();
            _receiveLoopCts = new CancellationTokenSource();

            ThreadSafeFileLogger.Log("Passive receive loop starting");
            _receiveLoopTask = Task.Run(() => PassiveReceiveLoopAsync(_receiveLoopCts.Token));
        }

        private async Task PassiveReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[1024];

            while (!token.IsCancellationRequested)
            {
                if (_webSocket == null)
                {
                    await Task.Delay(250, token);
                    continue;
                }

                try
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Server closed WebSocket: {result.CloseStatus} {result.CloseStatusDescription}");
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        var snippet = message.Length > 200 ? message.Substring(0, 200) + "..." : message;
                        ThreadSafeFileLogger.Log($"Received server message ({result.Count} bytes): {snippet}");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ThreadSafeFileLogger.LogWebSocketException("Passive receive loop", ex);
                    await Task.Delay(1000, token);
                }
            }
        }

        private void StopPassiveReceiveLoop()
        {
            if (_receiveLoopCts == null) return;

            try
            {
                ThreadSafeFileLogger.Log("Stopping passive receive loop");
                _receiveLoopCts.Cancel();
                _receiveLoopTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException ex)
            {
                ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Error stopping receive loop: {ex.InnerException?.Message ?? ex.Message}");
            }
            finally
            {
                ThreadSafeFileLogger.Log("Passive receive loop stopped");
                _receiveLoopCts.Dispose();
                _receiveLoopCts = null;
                _receiveLoopTask = null;
            }
        }

        private void WriteUrlFilesFromResponse(JsonElement root)
        {
            try
            {
                var config = ConfigManager.Get();
                if (config.UrlFiles == null) return;

                var runUrlFile = config.UrlFiles.RunUrlFile;
                var groupUrlFile = config.UrlFiles.GroupUrlFile;

                if (!string.IsNullOrEmpty(runUrlFile) && root.TryGetProperty("run_url", out var runUrlProp))
                {
                    var runUrl = _serverBaseUrl + runUrlProp.GetString();
                    WriteUrlFile(runUrlFile, runUrl);
                }

                if (!string.IsNullOrEmpty(groupUrlFile) && root.TryGetProperty("group_url", out var groupUrlProp))
                {
                    var groupUrl = _serverBaseUrl + groupUrlProp.GetString();
                    WriteUrlFile(groupUrlFile, groupUrl);
                }
            }
            catch (Exception ex)
            {
                ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Error writing URL files from response: {ex.Message}");
            }
        }

        private Dictionary<string, object> GetUserMetadata()
        {
            try
            {
                var config = ConfigManager.Get();
                return BuildMetadataDictionary(config.Metadata);
            }
            catch (Exception ex)
            {
                ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Failed to get user metadata: {ex.Message}");
            }
            return new Dictionary<string, object>();
        }

        private object GetGroupData()
        {
            try
            {
                var config = ConfigManager.Get();
                if (config.Group == null || string.IsNullOrWhiteSpace(config.Group.Name))
                    return null;

                return new
                {
                    name = config.Group.Name,
                    metadata = BuildMetadataDictionary(config.Group.Metadata)
                };
            }
            catch (Exception ex)
            {
                ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Failed to get group metadata: {ex.Message}");
                return null;
            }
        }

        private static Dictionary<string, object> BuildMetadataDictionary(IEnumerable<MetadataEntry> entries)
        {
            var metadata = new Dictionary<string, object>();
            if (entries == null) return metadata;

            foreach (var entry in entries)
            {
                if (entry == null) continue;

                var expandedName = VarExpander.Expand(entry.Name ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(expandedName)) continue;

                var expandedValue = VarExpander.Expand(entry.Value ?? string.Empty);
                var expandedUrl = entry.Url != null ? VarExpander.Expand(entry.Url) : null;

                metadata[expandedName] = new
                {
                    value = expandedValue,
                    url = expandedUrl
                };
            }
            return metadata;
        }

        public async Task SendRunFinished()
        {
            // Flush all pending events before finishing
            await FlushBatchAsync();

            var data = new
            {
                type = "run_finished",
                run_id = _runId,
                status = "finished"
            };

            await SendWebSocketMessage(data);

            // Gracefully close the WebSocket after run_finished to ensure server receives the message
            await CloseAsync();
        }

        /// <summary>
        /// Queue a test case started event for batched sending.
        /// </summary>
        public void QueueTestCaseStarted(string tcFullName, string nunitTestId, string startTime = null)
        {
            var tcId = nunitTestId;

            _testCaseIds[tcFullName] = tcId;
            if (!string.IsNullOrEmpty(nunitTestId))
            {
                _testIdToTcId[nunitTestId] = tcId;
            }

            var eventData = new Dictionary<string, object>
            {
                { "event_type", "test_case_started" },
                { "tc_full_name", tcFullName },
                { "tc_id", tcId },
                { "tc_meta", new Dictionary<string, object>
                    {
                        { "status", "running" },
                        { "start_time", startTime ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }
                    }
                }
            };

            QueueEvent(new BatchEvent { EventType = "test_case_started", Data = eventData, TestCaseId = tcId });
        }

        /// <summary>
        /// Queue a test case finished event for batched sending.
        /// Automatically flushes pending events for this test case first.
        /// </summary>
        public async Task QueueTestCaseFinishedAsync(string nunitTestId, string result)
        {
            // Flush all pending events for this test case to ensure ordering
            await FlushEventsForTestCaseAsync(nunitTestId);

            string standardizedStatus = ConvertNUnitResultAndErrorFlagToStatus(result, nunitTestId);
            string tcId = nunitTestId;

            if (string.IsNullOrEmpty(tcId))
            {
                ThreadSafeFileLogger.LogWebSocketConnectionFailed($"tc_id (NUnit test ID) is empty for test case");
                return;
            }

            var eventData = new Dictionary<string, object>
            {
                { "event_type", "test_case_finished" },
                { "tc_id", tcId },
                { "status", standardizedStatus }
            };

            // For test case finished, send immediately (after flushing its pending events)
            QueueEvent(new BatchEvent { EventType = "test_case_finished", Data = eventData, TestCaseId = tcId });
            await FlushBatchAsync();
        }

        /// <summary>
        /// Async method for test case started - queues and flushes for immediate sending.
        /// </summary>
        public async Task SendTestCaseStarted(string tcFullName, string nunitTestId, string startTime = null)
        {
            QueueTestCaseStarted(tcFullName, nunitTestId, startTime);
            // For TC start, we want it sent quickly so trigger a flush
            await FlushBatchAsync();
        }

        /// <summary>
        /// Async method for test case finished.
        /// </summary>
        public async Task SendTestCaseFinished(string nunitTestId, string result)
        {
            await QueueTestCaseFinishedAsync(nunitTestId, result);
        }

        public string GetTcIdFromTestId(string nunitTestId)
        {
            return _testIdToTcId.TryGetValue(nunitTestId, out var tcId) ? tcId : null;
        }

        public string GetTcIdFromFullName(string tcFullName)
        {
            return _testCaseIds.TryGetValue(tcFullName, out var tcId) ? tcId : null;
        }

        private string ConvertNUnitResultAndErrorFlagToStatus(string nunitResult, string nunitTestId)
        {
            if (!string.IsNullOrWhiteSpace(nunitTestId) &&
                _testCasesWithErrors.TryGetValue(nunitTestId, out var hasError) && hasError)
            {
                return "error";
            }

            if (string.IsNullOrEmpty(nunitResult)) return "failed";

            switch (nunitResult.ToLower())
            {
                case "passed": return "passed";
                case "failed": return "failed";
                case "skipped": return "skipped";
                case "inconclusive": return "failed";
                default:
                    ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Unexpected NUnit result: {nunitResult}, defaulting to failed");
                    return "failed";
            }
        }

        /// <summary>
        /// Queue an exception event for batched sending.
        /// </summary>
        public void QueueException(
            string nunitTestId,
            string message,
            string stackTrace,
            string exceptionType = null,
            IEnumerable<string> lines = null,
            bool isError = false,
            string timestamp = null)
        {
            if (string.IsNullOrEmpty(_runId) || string.IsNullOrWhiteSpace(nunitTestId) || string.IsNullOrWhiteSpace(stackTrace))
                return;

            if (isError)
            {
                _testCasesWithErrors[nunitTestId] = true;
            }

            string tcId = nunitTestId;
            if (string.IsNullOrEmpty(tcId)) return;

            var normalizedLines = lines != null
                ? new List<string>(lines)
                : new List<string>(stackTrace.Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));

            var eventData = new Dictionary<string, object>
            {
                { "event_type", "exception" },
                { "tc_id", tcId },
                { "timestamp", timestamp ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
                { "message", message },
                { "exception_type", exceptionType },
                { "is_error", isError },
                { "stack_trace", normalizedLines }
            };

            QueueEvent(new BatchEvent { EventType = "exception", Data = eventData, TestCaseId = tcId });
        }

        /// <summary>
        /// Async method for sending exception (queues for batching).
        /// </summary>
        public Task SendExceptionAsync(
            string nunitTestId,
            string message,
            string stackTrace,
            string exceptionType = null,
            IEnumerable<string> lines = null,
            bool isError = false,
            string timestamp = null)
        {
            QueueException(nunitTestId, message, stackTrace, exceptionType, lines, isError, timestamp);
            return Task.CompletedTask;
        }

        public void SendException(
            string nunitTestId,
            string message,
            string stackTrace,
            string exceptionType = null,
            IEnumerable<string> lines = null,
            bool isError = false,
            string timestamp = null)
        {
            QueueException(nunitTestId, message, stackTrace, exceptionType, lines, isError, timestamp);
        }

        /// <summary>
        /// Queue a log message for batched sending.
        /// </summary>
        public void QueueLogMessage(string message, string component, string channel = null, string dir = null, string nunitTestId = null, string timestamp = null, string phase = null)
        {
            if (string.IsNullOrEmpty(_runId) || string.IsNullOrEmpty(message)) return;

            if (!string.IsNullOrEmpty(dir) && dir != "tx" && dir != "rx")
            {
                throw new ArgumentException("dir must be 'tx' or 'rx' if provided", nameof(dir));
            }

            var logEntry = new LogEntry
            {
                timestamp = timestamp ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                message = message,
                component = component ?? Environment.MachineName,
                channel = channel,
                dir = dir,
                phase = phase
            };

            // Create or add to existing log batch event for this test case
            QueueLogEntry(nunitTestId, logEntry);
        }

        /// <summary>
        /// Queue a simple message.
        /// </summary>
        public void QueueMessage(string message, string source = "console", string testCaseId = null)
        {
            QueueMessage(message, source, testCaseId, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        }

        public void QueueMessage(string message, string source, string testCaseId, string timestamp)
        {
            QueueLogMessage(message, Environment.MachineName, source, null, testCaseId, timestamp);
        }

        private void QueueLogEntry(string nunitTestId, LogEntry entry)
        {
            string tcId = nunitTestId ?? "global";

            var eventData = new Dictionary<string, object>
            {
                { "event_type", "log_batch" },
                { "tc_id", tcId },
                { "entries", new List<LogEntry> { entry } }
            };

            var batchEvent = new BatchEvent
            {
                EventType = "log_batch",
                Data = eventData,
                TestCaseId = tcId,
                LogEntry = entry
            };

            QueueEvent(batchEvent);
        }

        private void QueueEvent(BatchEvent evt)
        {
            _eventQueue.Enqueue(evt);

            // Estimate size and trigger flush if buffer is getting large
            var estimatedSize = Interlocked.Add(ref _estimatedBatchSize, EstimateEventSize(evt));
            if (estimatedSize >= MaxBatchSizeBytes)
            {
                // Fire and forget async flush
                _ = Task.Run(async () => await FlushBatchAsync());
            }
        }

        private static int EstimateEventSize(BatchEvent evt)
        {
            // Rough estimate: base overhead + message content
            int size = 100; // Base JSON overhead
            if (evt.LogEntry != null)
            {
                size += (evt.LogEntry.message?.Length ?? 0) + (evt.LogEntry.component?.Length ?? 0) + 50;
            }
            else if (evt.Data != null)
            {
                size += 200; // Estimated size for other event types
            }
            return size;
        }

        private void OnBatchTimerTick(object state)
        {
            // Don't block the timer thread - use a dedicated task
            _ = Task.Run(async () => await FlushBatchAsync());
        }

        private void OnHeartbeatTimerTick(object state)
        {
            // Send heartbeat if no message has been sent recently
            _ = Task.Run(async () => await SendHeartbeatIfNeededAsync());
        }

        /// <summary>
        /// Send a heartbeat message to keep the connection alive.
        /// </summary>
        private async Task SendHeartbeatIfNeededAsync()
        {
            if (string.IsNullOrEmpty(_runId)) return;
            if (_webSocket?.State != WebSocketState.Open) return;

            var timeSinceLastSend = DateTime.UtcNow - _lastSendTime;
            if (timeSinceLastSend.TotalMilliseconds < HeartbeatIntervalMs - 1000)
            {
                // Recent activity, no heartbeat needed
                return;
            }

            try
            {
                var heartbeat = new { type = "heartbeat", run_id = _runId };
                await SendWebSocketMessage(heartbeat);
                ThreadSafeFileLogger.Log($"Heartbeat sent (last activity {timeSinceLastSend.TotalSeconds:F1}s ago)");
            }
            catch (Exception ex)
            {
                ThreadSafeFileLogger.LogWebSocketException("Heartbeat", ex);
            }
        }

        /// <summary>
        /// Flush all pending events as a batch message.
        /// </summary>
        public async Task FlushBatchAsync()
        {
            if (_eventQueue.IsEmpty || string.IsNullOrEmpty(_runId)) return;

            await _sendSemaphore.WaitAsync();
            try
            {
                // Collect all events (up to max)
                var events = new List<object>();
                var logBatches = new Dictionary<string, List<LogEntry>>();

                int count = 0;
                while (_eventQueue.TryDequeue(out var evt) && count < MaxEventsPerBatch)
                {
                    count++;

                    // Consolidate log entries for the same test case
                    if (evt.EventType == "log_batch" && evt.LogEntry != null)
                    {
                        var tcId = evt.TestCaseId ?? "global";
                        if (!logBatches.TryGetValue(tcId, out var entries))
                        {
                            entries = new List<LogEntry>();
                            logBatches[tcId] = entries;
                        }
                        entries.Add(evt.LogEntry);
                    }
                    else
                    {
                        events.Add(evt.Data);
                    }
                }

                // Add consolidated log batches
                foreach (var kvp in logBatches)
                {
                    events.Add(new Dictionary<string, object>
                    {
                        { "event_type", "log_batch" },
                        { "tc_id", kvp.Key },
                        { "entries", kvp.Value }
                    });
                }

                Interlocked.Exchange(ref _estimatedBatchSize, 0);

                if (events.Count == 0) return;

                var batchMessage = new
                {
                    type = "batch",
                    run_id = _runId,
                    events = events
                };

                await SendWebSocketMessageDirect(batchMessage);
                _lastSendTime = DateTime.UtcNow;
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }

        /// <summary>
        /// Flush events for a specific test case (used before test_case_finished).
        /// </summary>
        private async Task FlushEventsForTestCaseAsync(string nunitTestId)
        {
            if (string.IsNullOrEmpty(nunitTestId)) return;

            // First do a general flush to clear the queue
            await FlushBatchAsync();

            // Small delay to allow any in-flight operations
            await Task.Delay(10);
        }

        private async Task SendWebSocketMessage(object data)
        {
            await _sendSemaphore.WaitAsync();
            try
            {
                await SendWebSocketMessageDirect(data);
                _lastSendTime = DateTime.UtcNow;
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }

        private async Task SendWebSocketMessageDirect(object data)
        {
            var messageType = GetMessageType(data);
            var messageId = Interlocked.Increment(ref _messageSequence);

            if (_webSocket?.State != WebSocketState.Open)
            {
                var stateSnapshot = DescribeSocketState();
                ThreadSafeFileLogger.LogWebSocketNotOpen(stateSnapshot);
                ThreadSafeFileLogger.LogSendFailed($"Send[{messageId}] skipped (type={messageType})");
                return;
            }

            string json;
            byte[] bytes;
            try
            {
                json = JsonSerializer.Serialize(data, _jsonOptions);
                bytes = Encoding.UTF8.GetBytes(json);
            }
            catch (Exception ex)
            {
                ThreadSafeFileLogger.LogWebSocketException($"Serialize message {messageId} ({messageType})", ex);
                return;
            }

            ThreadSafeFileLogger.LogSendStarting(messageId, messageType, DescribeSocketState());

            try
            {
                if (_webSocket?.State != WebSocketState.Open)
                {
                    ThreadSafeFileLogger.LogWebSocketNotOpen(DescribeSocketState());
                    return;
                }

                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                ThreadSafeFileLogger.LogSendCompleted(messageId, messageType, bytes.Length);
            }
            catch (Exception ex)
            {
                ThreadSafeFileLogger.LogSendFailed(ex.Message);
                ThreadSafeFileLogger.LogWebSocketException($"Send[{messageId}] ({messageType})", ex);
            }
        }

        /// <summary>
        /// Flushes all pending messages and waits for completion.
        /// </summary>
        public async Task FlushAllMessagesAsync()
        {
            await FlushBatchAsync();
        }

        /// <summary>
        /// Flushes all messages synchronously.
        /// </summary>
        public void FlushAllMessages()
        {
            FlushBatchAsync().Wait();
        }

        /// <summary>
        /// Waits for pending operations to complete.
        /// </summary>
        public async Task WaitForFlushCompleteAsync()
        {
            await FlushBatchAsync();
        }

        /// <summary>
        /// Uploads an attachment for a test case (synchronous version).
        /// </summary>
        public bool UploadAttachment(string nunitTestId, string filePath, string description = null)
        {
            return UploadAttachmentAsync(nunitTestId, filePath, description).GetAwaiter().GetResult();
        }

        private void WriteUrlFile(string filePath, string url)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(filePath, url);
                ThreadSafeFileLogger.LogMessageSent($"Wrote URL file: {filePath}");
            }
            catch (Exception ex)
            {
                ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Failed to write URL file {filePath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Uploads an attachment for a test case.
        /// </summary>
        public async Task<bool> UploadAttachmentAsync(string nunitTestId, string filePath, string description = null)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Attachment file not found: {filePath}");
                    return false;
                }

                string tcId = GetTcIdFromTestId(nunitTestId) ?? GetTcIdFromFullName(nunitTestId);
                if (tcId == null)
                {
                    ThreadSafeFileLogger.LogWebSocketConnectionFailed($"tc_id not found for test case: {nunitTestId}");
                    return false;
                }

                var fileName = Path.GetFileName(filePath);
                var uploadUrl = $"{_serverBaseUrl}/api/attachments/{_runId}/{tcId}/upload";

                using (var form = new MultipartFormDataContent())
                using (var fileContent = new ByteArrayContent(await ReadAllBytesAsync(filePath)))
                {
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    form.Add(fileContent, "attachment", fileName);

                    if (!string.IsNullOrEmpty(description))
                    {
                        form.Add(new StringContent(description), "description");
                    }

                    var response = await _httpClient.PostAsync(uploadUrl, form);

                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }
                    else
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Attachment upload failed: {response.StatusCode} - {errorContent}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Attachment upload error: {ex.Message}");
                return false;
            }
        }

        private static async Task<byte[]> ReadAllBytesAsync(string path)
        {
#if NET462
            return await Task.Run(() => File.ReadAllBytes(path));
#else
            return await File.ReadAllBytesAsync(path);
#endif
        }

        /// <summary>
        /// Gracefully close the WebSocket connection with a normal close handshake.
        /// Should be called after SendRunFinished() to ensure the server receives all messages.
        /// </summary>
        public async Task CloseAsync()
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                try
                {
                    ThreadSafeFileLogger.Log("Initiating graceful WebSocket close");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Run finished", cts.Token);
                    ThreadSafeFileLogger.Log("WebSocket closed gracefully");
                }
                catch (OperationCanceledException)
                {
                    ThreadSafeFileLogger.Log("WebSocket close timed out after 5 seconds");
                }
                catch (Exception ex)
                {
                    ThreadSafeFileLogger.Log($"Error during WebSocket close: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                StopPassiveReceiveLoop();
                _batchTimer?.Dispose();
                _heartbeatTimer?.Dispose();
                ThreadSafeFileLogger.Log("Disposing WebSocketHelper");
                if (_webSocket != null)
                {
                    ThreadSafeFileLogger.LogWebSocketStateDetails("Dispose", _webSocket.State, _webSocket.CloseStatus, _webSocket.CloseStatusDescription);
                    // Try graceful close if still open
                    if (_webSocket.State == WebSocketState.Open)
                    {
                        try
                        {
                            ThreadSafeFileLogger.Log("Attempting graceful close during Dispose");
                            _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None)
                                .GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            ThreadSafeFileLogger.Log($"Graceful close failed during Dispose: {ex.Message}");
                        }
                    }
                }
                _webSocket?.Dispose();
                _httpClient?.Dispose();
                _sendSemaphore?.Dispose();
                _disposed = true;
            }
        }

        private static string GetMessageType(object data)
        {
            if (data == null) return "null";

            if (data is IDictionary<string, object> dict && dict.TryGetValue("type", out var value) && value != null)
                return value.ToString();

            var typeProperty = data.GetType().GetProperty("type");
            if (typeProperty != null)
            {
                var propertyValue = typeProperty.GetValue(data);
                if (propertyValue != null)
                    return propertyValue.ToString();
            }

            return "unknown";
        }

        private string DescribeSocketState()
        {
            if (_webSocket == null) return "null";

            var closeStatus = _webSocket.CloseStatus?.ToString() ?? "null";
            var closeDescription = _webSocket.CloseStatusDescription ?? "null";
            return $"State={_webSocket.State}, CloseStatus={closeStatus}, CloseDescription={closeDescription}";
        }
    }

    /// <summary>
    /// Internal class representing a queued event for batching.
    /// </summary>
    internal class BatchEvent
    {
        public string EventType { get; set; }
        public Dictionary<string, object> Data { get; set; }
        public string TestCaseId { get; set; }
        public LogEntry LogEntry { get; set; }  // For log_batch events, store the entry directly
    }

    /// <summary>
    /// Log entry for WebSocket messages. Only includes non-null fields when serialized.
    /// </summary>
    public class LogEntry
    {
        public string timestamp { get; set; }
        public string message { get; set; }
        public string component { get; set; }
        public string channel { get; set; }
        public string dir { get; set; }
        public string phase { get; set; }
    }
}
