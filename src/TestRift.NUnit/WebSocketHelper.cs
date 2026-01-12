using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TestRift.NUnit
{
    /// <summary>
    /// Helper class for managing WebSocket connections and message pooling
    /// </summary>
    public class WebSocketHelper : IDisposable
    {
        private readonly string _serverBaseUrl;
        private string _runId;  // Set by server response after run_started
        private ClientWebSocket _webSocket;
        private readonly ConcurrentQueue<LogEntry> _messageQueue = new ConcurrentQueue<LogEntry>();
        private readonly Timer _batchTimer;
        private bool _disposed = false;
        private readonly SemaphoreSlim _flushSemaphore = new SemaphoreSlim(1, 1);
        private volatile bool _isFlushing = false;
        private readonly SemaphoreSlim _sendSemaphore = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _testCaseSendLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
        private readonly HttpClient _httpClient;
        private CancellationTokenSource _receiveLoopCts;
        private Task _receiveLoopTask;
        private long _messageSequence;
        // Tracks test cases that have reported unexpected/unhandled errors (is_error = true)
        // so we can map their final status to "error" instead of "failed".
        private readonly ConcurrentDictionary<string, bool> _testCasesWithErrors = new ConcurrentDictionary<string, bool>();

        public WebSocketHelper(string serverBaseUrl = null)
        {
            if (!string.IsNullOrWhiteSpace(serverBaseUrl))
            {
                _serverBaseUrl = serverBaseUrl;
            }
            else
            {
                // Prefer YAML config if available; otherwise fall back to localhost.
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
            _runId = null;  // Will be set by server response

            // Initialize batch timer to send messages every 1 second
            _batchTimer = new Timer(SendBatchedMessages, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            // Initialize HTTP client for attachment uploads
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

                // Debug: Write to file instead of console
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

            // Build message dictionary, only including run_id if it's set
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

            // Wait for server response with run_id and URLs
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
                    ThreadSafeFileLogger.LogWebSocketStateDetails("wait_run_started", _webSocket?.State, _webSocket?.CloseStatus, _webSocket?.CloseStatusDescription);
                    throw new InvalidOperationException($"WebSocket connection is not open (state: {stateSnapshot})");
                }

                var buffer = new byte[4096];
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // 10 second timeout

                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("type", out var typeProp) &&
                        typeProp.GetString() == "run_started_response")
                    {
                        // Check for error response
                        if (root.TryGetProperty("error", out var errorProp))
                        {
                            var errorMessage = errorProp.GetString();
                            ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Run start failed: {errorMessage}");
                            throw new InvalidOperationException($"Run start failed: {errorMessage}");
                        }

                        // Get run_id from server
                        if (root.TryGetProperty("run_id", out var runIdProp))
                        {
                            _runId = runIdProp.GetString();
                            ThreadSafeFileLogger.LogMessageSent($"Received run_id from server: {_runId}");
                        }

                        // Write URL files
                        WriteUrlFilesFromResponse(root);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                ThreadSafeFileLogger.LogWebSocketConnectionFailed("Timeout waiting for run_started_response");
                throw new InvalidOperationException("Timeout waiting for run_started_response from server");
            }
            // Don't catch other exceptions - let them propagate to abort the test run
        }

        private void StartPassiveReceiveLoop()
        {
            if (_receiveLoopTask != null && !_receiveLoopTask.IsCompleted)
            {
                return;
            }

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
                        ThreadSafeFileLogger.LogWebSocketStateDetails("Server close frame", result.CloseStatus.HasValue ? WebSocketState.CloseSent : _webSocket?.State, result.CloseStatus, result.CloseStatusDescription);
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
                    ThreadSafeFileLogger.Log("Passive receive loop canceled");
                    break;
                }
                catch (ObjectDisposedException)
                {
                    ThreadSafeFileLogger.Log("Passive receive loop disposed socket");
                    break;
                }
                catch (Exception ex)
                {
                    ThreadSafeFileLogger.LogWebSocketException("Passive receive loop", ex);
                    if (ex is WebSocketException wsEx)
                    {
                        ThreadSafeFileLogger.LogWebSocketStateDetails($"Passive receive loop fault (code={wsEx.WebSocketErrorCode})", _webSocket?.State, _webSocket?.CloseStatus, _webSocket?.CloseStatusDescription);
                    }
                    await Task.Delay(1000, token);
                }
            }
        }

        private void StopPassiveReceiveLoop()
        {
            if (_receiveLoopCts == null)
            {
                return;
            }

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
                if (config.UrlFiles == null)
                    return;

                var runUrlFile = config.UrlFiles.RunUrlFile;
                var groupUrlFile = config.UrlFiles.GroupUrlFile;

                // Write run URL file if configured
                if (!string.IsNullOrEmpty(runUrlFile) && root.TryGetProperty("run_url", out var runUrlProp))
                {
                    var runUrl = _serverBaseUrl + runUrlProp.GetString();
                    WriteUrlFile(runUrlFile, runUrl);
                }

                // Write group URL file if configured and group exists
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
                // If config is not loaded, use empty metadata
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
                {
                    return null;
                }

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
            if (entries == null)
            {
                return metadata;
            }

            foreach (var entry in entries)
            {
                if (entry == null)
                {
                    continue;
                }

                var expandedName = VarExpander.Expand(entry.Name ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(expandedName))
                {
                    continue;
                }

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
            var data = new
            {
                type = "run_finished",
                run_id = _runId,
                status = "finished"
            };

            await SendWebSocketMessage(data);
        }

        public async Task SendTestCaseStarted(string testCaseId, string fullName, string startTime = null)
        {
            var data = new
            {
                type = "test_case_started",
                run_id = _runId,
                test_case_id = testCaseId,
                test_case_meta = new
                {
                    status = "running",
                    start_time = startTime ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                }
            };

            await SendWebSocketMessage(data);
        }

        public async Task SendTestCaseFinished(string testCaseId, string result)
        {
            // First, flush all queued messages for this test case to ensure proper ordering
            await FlushMessagesForTestCase(testCaseId);

            // Convert NUnit result + any recorded error flag to standardized status format
            string standardizedStatus = ConvertNUnitResultAndErrorFlagToStatus(result, testCaseId);

            // Then send the test case finished message
            var data = new
            {
                type = "test_case_finished",
                run_id = _runId,
                test_case_id = testCaseId,
                status = standardizedStatus
            };

            await SendWebSocketMessage(data);
        }

        /// <summary>
        /// Convert NUnit result + any recorded error flag into the wire status.
        /// - If the test case has reported an unexpected/unhandled error (is_error = true), return "error".
        /// - Otherwise, map NUnit result to "passed" / "failed" / "skipped".
        /// </summary>
        private string ConvertNUnitResultAndErrorFlagToStatus(string nunitResult, string testCaseId)
        {
            // If we saw an is_error=true exception for this test case, treat it as "error"
            if (!string.IsNullOrWhiteSpace(testCaseId) &&
                _testCasesWithErrors.TryGetValue(testCaseId, out var hasError) &&
                hasError)
            {
                return "error";
            }

            if (string.IsNullOrEmpty(nunitResult))
                return "failed";

            // NUnit returns: "Passed", "Failed", "Skipped", "Inconclusive"
            // Convert to standardized status: "passed", "failed", "skipped", "failed"
            switch (nunitResult.ToLower())
            {
                case "passed":
                    return "passed";
                case "failed":
                    return "failed";
                case "skipped":
                    return "skipped";
                case "inconclusive":
                    return "failed"; // Inconclusive tests are treated as failed
                default:
                    // For any unexpected values, default to failed
                    ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Unexpected NUnit result: {nunitResult}, defaulting to failed");
                    return "failed";
            }
        }

        public Task SendExceptionAsync(
            string testCaseId,
            string message,
            string stackTrace,
            string exceptionType = null,
            IEnumerable<string> lines = null,
            bool isError = false,
            string timestamp = null)
        {
            if (string.IsNullOrEmpty(_runId) || string.IsNullOrWhiteSpace(testCaseId) || string.IsNullOrWhiteSpace(stackTrace))
            {
                return Task.CompletedTask;
            }

            // Remember that this test case had an unexpected/unhandled error so its final
            // status can be reported as "error" instead of "failed".
            if (isError)
            {
                _testCasesWithErrors[testCaseId] = true;
            }

            // Normalize the stack trace into a list of lines. This becomes the canonical representation.
            var normalizedLines = lines != null
                ? new List<string>(lines)
                : new List<string>(stackTrace.Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));

            var data = new
            {
                type = "exception",
                run_id = _runId,
                test_case_id = testCaseId,
                timestamp = timestamp ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                message,
                exception_type = exceptionType,
                is_error = isError,
                // stack_trace is a list of strings representing the full multiline stack trace
                stack_trace = normalizedLines
            };

            return SendWebSocketMessage(data);
        }

        public void SendException(
            string testCaseId,
            string message,
            string stackTrace,
            string exceptionType = null,
            IEnumerable<string> lines = null,
            bool isError = false,
            string timestamp = null)
        {
            _ = SendExceptionAsync(testCaseId, message, stackTrace, exceptionType, lines, isError, timestamp);
        }

        public void QueueMessage(string message, string source = "console", string testCaseId = null)
        {
            QueueMessage(message, source, testCaseId, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        }

        /// <summary>
        /// Queues a log message with full control over component, channel, and direction.
        /// </summary>
        /// <param name="message">The log message text</param>
        /// <param name="component">The component name</param>
        /// <param name="channel">Optional channel name</param>
        /// <param name="dir">Optional direction: "tx" or "rx"</param>
        /// <param name="testCaseId">Optional test case ID</param>
        /// <param name="timestamp">Optional timestamp (uses current time if not provided)</param>
        public void QueueLogMessage(string message, string component, string channel = null, string dir = null, string testCaseId = null, string timestamp = null, string phase = null)
        {
            if (string.IsNullOrEmpty(_runId) || string.IsNullOrEmpty(message)) return;

            // Validate dir if provided
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
                testCaseId = testCaseId,
                phase = phase
            };

            _messageQueue.Enqueue(logEntry);

            // Send immediately if we have a specific test case ID
            if (!string.IsNullOrEmpty(testCaseId))
            {
                // Use a per-test-case lock to ensure messages are sent in order
                var sendLock = _testCaseSendLocks.GetOrAdd(testCaseId, _ => new SemaphoreSlim(1, 1));
                _ = Task.Run(async () =>
                {
                    await sendLock.WaitAsync();
                    try
                    {
                        await SendLogBatch(testCaseId);
                    }
                    finally
                    {
                        sendLock.Release();
                    }
                });
            }
        }

        /// <summary>
        /// Legacy method: Queues a log message using source as channel.
        /// Kept for backward compatibility.
        /// </summary>
        public void QueueMessage(string message, string source, string testCaseId, string timestamp)
        {
            QueueLogMessage(message, Environment.MachineName, source, null, testCaseId, timestamp);
        }

        private async Task FlushMessagesForTestCase(string testCaseId)
        {
            if (string.IsNullOrEmpty(testCaseId)) return;

            var messages = new List<LogEntry>();
            var remainingMessages = new List<LogEntry>();

            // Collect all messages for this test case
            while (_messageQueue.TryDequeue(out var message))
            {
                if (message.testCaseId == testCaseId)
                {
                    messages.Add(message);
                }
                else
                {
                    remainingMessages.Add(message);
                }
            }

            // Put back messages that don't belong to this test case
            foreach (var message in remainingMessages)
            {
                _messageQueue.Enqueue(message);
            }

            // Send all messages for this test case
            if (messages.Count > 0)
            {
                var data = new
                {
                    type = "log_batch",
                    run_id = _runId,
                    test_case_id = testCaseId,
                    entries = messages
                };

                await SendWebSocketMessage(data);
            }
        }

        private async Task SendWebSocketMessage(object data)
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
                json = JsonSerializer.Serialize(data);
                bytes = Encoding.UTF8.GetBytes(json);
            }
            catch (Exception ex)
            {
                ThreadSafeFileLogger.LogWebSocketException($"Serialize message {messageId} ({messageType})", ex);
                return;
            }

            ThreadSafeFileLogger.LogSendStarting(messageId, messageType, DescribeSocketState());

            await _sendSemaphore.WaitAsync();
            try
            {
                // Double-check state after acquiring lock
                if (_webSocket?.State != WebSocketState.Open)
                {
                    ThreadSafeFileLogger.LogWebSocketNotOpen(DescribeSocketState());
                    ThreadSafeFileLogger.LogSendFailed($"Send[{messageId}] skipped after lock (type={messageType})");
                    return;
                }

                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                ThreadSafeFileLogger.LogSendCompleted(messageId, messageType, bytes.Length);
                ThreadSafeFileLogger.LogMessageSent(json.Substring(0, Math.Min(100, json.Length)) + "...");
            }
            catch (Exception ex)
            {
                ThreadSafeFileLogger.LogSendFailed(ex.Message);
                ThreadSafeFileLogger.LogWebSocketException($"Send[{messageId}] ({messageType})", ex);
                ThreadSafeFileLogger.LogWebSocketStateDetails($"Send[{messageId}] failure", _webSocket?.State, _webSocket?.CloseStatus, _webSocket?.CloseStatusDescription);
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }

        private async Task SendLogBatch(string testCaseId)
        {
            if (_messageQueue.IsEmpty) return;

            var messages = new List<LogEntry>();
            var remainingMessages = new List<LogEntry>();

            // Collect messages for this specific test case
            while (_messageQueue.TryDequeue(out var message))
            {
                if (message.testCaseId == testCaseId)
                {
                    messages.Add(message);
                }
                else
                {
                    remainingMessages.Add(message);
                }
            }

            // Put back messages that don't belong to this test case
            foreach (var message in remainingMessages)
            {
                _messageQueue.Enqueue(message);
            }

            if (messages.Count == 0) return;

            var data = new
            {
                type = "log_batch",
                run_id = _runId,
                test_case_id = testCaseId,
                entries = messages
            };

            await SendWebSocketMessage(data);
        }

        private async void SendBatchedMessages(object state)
        {
            // This method is called by the timer for general console output
            // Individual test case output is sent immediately
            await FlushAllMessagesAsync();
        }


        /// <summary>
        /// Flushes all pending messages and waits for completion
        /// </summary>
        public async Task FlushAllMessagesAsync()
        {
            await _flushSemaphore.WaitAsync();
            try
            {
                _isFlushing = true;

                var messages = new List<LogEntry>();
                while (_messageQueue.TryDequeue(out var message))
                {
                    messages.Add(message);
                }

                if (messages.Count > 0)
                {
                var data = new
                {
                    event_type = "log_batch",
                    run_id = _runId,
                    logs = messages
                };

                    await SendWebSocketMessage(data);
                }
            }
            finally
            {
                _isFlushing = false;
                _flushSemaphore.Release();
            }
        }

        /// <summary>
        /// Waits for all pending operations to complete
        /// </summary>
        public async Task WaitForFlushCompleteAsync()
        {
            await _flushSemaphore.WaitAsync();
            try
            {
                // Wait for any ongoing flush to complete
                while (_isFlushing)
                {
                    await Task.Delay(10);
                }
            }
            finally
            {
                _flushSemaphore.Release();
            }
        }

        /// <summary>
        /// Flushes all messages and waits for completion synchronously
        /// </summary>
        public void FlushAllMessages()
        {
            FlushAllMessagesAsync().Wait();
        }

        /// <summary>
        /// Uploads an attachment for a test case (synchronous version)
        /// </summary>
        public bool UploadAttachment(string testCaseId, string filePath, string description = null)
        {
            return UploadAttachmentAsync(testCaseId, filePath, description).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Writes a URL to a file. Creates the directory if it doesn't exist.
        /// </summary>
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
        /// Uploads an attachment for a test case
        /// </summary>
        public async Task<bool> UploadAttachmentAsync(string testCaseId, string filePath, string description = null)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Attachment file not found: {filePath}");
                    return false;
                }

                var fileName = Path.GetFileName(filePath);
                var uploadUrl = $"{_serverBaseUrl}/api/attachments/{_runId}/{testCaseId}/upload";

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

        public void Dispose()
        {
            if (!_disposed)
            {
                StopPassiveReceiveLoop();
                _batchTimer?.Dispose();
                ThreadSafeFileLogger.Log("Disposing WebSocketHelper");
                if (_webSocket != null)
                {
                    ThreadSafeFileLogger.LogWebSocketStateDetails("Dispose", _webSocket.State, _webSocket.CloseStatus, _webSocket.CloseStatusDescription);
                }
                _webSocket?.Dispose();
                _httpClient?.Dispose();
                _flushSemaphore?.Dispose();
                _sendSemaphore?.Dispose();

                // Dispose all per-test-case locks
                foreach (var kvp in _testCaseSendLocks)
                {
                    kvp.Value?.Dispose();
                }
                _testCaseSendLocks.Clear();

                _disposed = true;
            }
        }

        private static string GetMessageType(object data)
        {
            if (data == null)
            {
                return "null";
            }

            if (data is IDictionary<string, object> dict && dict.TryGetValue("type", out var value) && value != null)
            {
                return value.ToString();
            }

            var typeProperty = data.GetType().GetProperty("type");
            if (typeProperty != null)
            {
                var propertyValue = typeProperty.GetValue(data);
                if (propertyValue != null)
                {
                    return propertyValue.ToString();
                }
            }

            return "unknown";
        }

        private string DescribeSocketState()
        {
            if (_webSocket == null)
            {
                return "null";
            }

            var closeStatus = _webSocket.CloseStatus?.ToString() ?? "null";
            var closeDescription = _webSocket.CloseStatusDescription ?? "null";
            return $"State={_webSocket.State}, CloseStatus={closeStatus}, CloseDescription={closeDescription}";
        }
    }
}
