using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace TestRift.NUnit
{
    public class TestAttachment
    {
        public string FilePath { get; set; }
        public string Description { get; set; }
        public string TestCaseId { get; set; }
        public bool WaitForUpload { get; set; } = false;
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Assembly, AllowMultiple = false)]
    public class TRLoggerAttribute : Attribute, ITestAction
    {
        private TextWriter _originalConsole;
        private LogTextWriter _fileWriter;
        private string _logFilePath;
        private WebSocketHelper _webSocketHelper;
        private static WebSocketHelper _sharedWebSocketHelper;
        private static bool _runStarted = false;
        private static readonly object _runLock = new object();
        public void BeforeTest(ITest test)
        {
            ThreadSafeFileLogger.LogBeforeTest(test.FullName, test.IsSuite, test.Parent?.FullName);

            if (!test.IsSuite)
            {
                // Clear any previous attachments for this test
                TestContextWrapper.ClearAttachments();

                // This is an individual test case
                ThreadSafeFileLogger.LogStartTest(test.FullName);

                lock (_runLock)
                {
                    _webSocketHelper = _sharedWebSocketHelper; // Use the shared helper
                }

                // Set the WebSocket helper for immediate uploads
                TestContextWrapper.SetWebSocketHelper(_webSocketHelper);

                // Send test case started FIRST, synchronously
                if (_webSocketHelper != null)
                {
                    _webSocketHelper.SendTestCaseStarted(
                        test.FullName,
                        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")).Wait();
                }

                // Now create LogTextWriter and start logging
                string testCaseId = GenerateTestCaseId(test.FullName);
                string safeFileName = MakeSafeFileName(test.FullName);
                _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{safeFileName}.log");
                if (File.Exists(_logFilePath)) File.Delete(_logFilePath);

                _fileWriter = new LogTextWriter(_logFilePath, testCaseId: testCaseId, webSocketHelper: _webSocketHelper);
                _originalConsole = Console.Out;
                Console.SetOut(_fileWriter);

                _fileWriter.WriteLine($"[START] {test.FullName}");

                // Activity hook (does not emit teardown marker here)
                TeardownMonitor.OnActivity(
                    GenerateTestCaseId(test.FullName),
                    _webSocketHelper,
                    activity: "TRLoggerAttribute.BeforeTest",
                    aboutToSendLog: false);
            }
        }

        public void AfterTest(ITest test)
        {
            if (!test.IsSuite)
            {
                // This is a test case
                ThreadSafeFileLogger.LogEndTest(test.FullName, TestContext.CurrentContext.Result.Outcome.ToString());

                // Ensure stack trace is reported only once BEFORE we emit teardown-phase console lines.
                // (The UI's teardown grouping is order-based, so exceptions must be emitted first.)
                var exceptionTimestamp = DateTime.UtcNow.AddMilliseconds(-5).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                TeardownMonitor.OnAfterTest(GenerateTestCaseId(test.FullName), _webSocketHelper, exceptionTimestamp);

                _fileWriter?.WriteLine($"[END] {test.FullName} => {TestContext.CurrentContext.Result.Outcome}");
                _fileWriter?.Dispose();
                _fileWriter = null;

                if (_originalConsole != null)
                {
                    Console.SetOut(_originalConsole);
                    _originalConsole = null;
                }
                else
                {
                    ThreadSafeFileLogger.Log($"[WARN] Original console writer missing when ending {test.FullName}; skipping Console.SetOut");
                }

                TeardownMonitor.OnActivity(
                    GenerateTestCaseId(test.FullName),
                    _webSocketHelper,
                    activity: "TRLoggerAttribute.AfterTest",
                    aboutToSendLog: false);

                // Set the WebSocket helper and process any tracked attachments
                TestContextWrapper.SetWebSocketHelper(_webSocketHelper);

                // Process any tracked attachments that couldn't be uploaded immediately
                HandleTestAttachments(test).GetAwaiter().GetResult();

                // Wait for all uploads to complete before leaving AfterTest
                TestContextWrapper.WaitForUploadsToComplete();

                // Send test case finished
                if (_webSocketHelper != null)
                {
                    _ = Task.Run(async () =>
                    {
                        await _webSocketHelper.SendTestCaseFinished(
                            GenerateTestCaseId(test.FullName),
                            TestContext.CurrentContext.Result.Outcome.ToString());
                    });
                }

            }
        }

        private string GenerateTestCaseId(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "test";

            // Keep dots for proper tree structure, only replace spaces and parentheses
            return fullName.Replace(" ", "_").Replace("(", "").Replace(")", "");
        }

        private string MakeSafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "test";

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
            {
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            return builder.Length > 0 ? builder.ToString() : "test";
        }



        private async Task HandleTestAttachments(ITest test)
        {
            try
            {
                // Get tracked attachments for this test and clear them
                var attachments = TestContextWrapper.GetAndClearAttachments();

                if (attachments.Count > 0)
                {
                    var testCaseId = GenerateTestCaseId(test.FullName);

                    // Upload each attachment asynchronously
                    foreach (var attachment in attachments)
                    {
                        if (_webSocketHelper != null)
                        {
                            try
                            {
                                await _webSocketHelper.UploadAttachmentAsync(
                                    testCaseId,
                                    attachment.FilePath,
                                    attachment.Description);
                            }
                            catch (Exception ex)
                            {
                                ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Error uploading attachment {Path.GetFileName(attachment.FilePath)}: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Error handling test attachments: {ex.Message}");
            }
        }

        public ActionTargets Targets => ActionTargets.Test;

        // Static methods called by RunHooks
        public static void NotifyRunStarted()
        {
            lock (_runLock)
            {
                if (!_runStarted)
                {
                    _runStarted = true;
                    _sharedWebSocketHelper = new WebSocketHelper();
                    try
                    {
                        _sharedWebSocketHelper.ConnectAsync().Wait();
                        // SendRunStarted now waits for server response with run_id and writes URL files
                        // If server returns an error (e.g., invalid run_id), this will throw and abort the test run
                        _sharedWebSocketHelper.SendRunStarted().Wait();
                    }
                    catch (AggregateException aggEx)
                    {
                        // Unwrap AggregateException to get the original exception
                        // This allows NUnit to properly abort the test run with a clear error message
                        var innerEx = aggEx.InnerException ?? aggEx;
                        ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Failed to start test run: {innerEx.Message}");
                        throw innerEx;
                    }

                    ThreadSafeFileLogger.LogRunStarted();
                }
            }
        }

        public static void NotifyRunFinished()
        {
            lock (_runLock)
            {
                if (_runStarted && _sharedWebSocketHelper != null)
                {
                    ThreadSafeFileLogger.LogRunFinished();

                    try
                    {
                        // Flush all pending messages and wait for completion
                        _sharedWebSocketHelper.FlushAllMessagesAsync().Wait();

                        // Send run finished message and wait for it to be sent
                        _sharedWebSocketHelper.SendRunFinished().Wait();

                        // Wait for run finished message to be sent
                        _sharedWebSocketHelper.WaitForFlushCompleteAsync().Wait();

                        // Give the server a moment to process the run_finished message
                        System.Threading.Thread.Sleep(1000);
                    }
                    catch (Exception ex)
                    {
                        ThreadSafeFileLogger.LogWebSocketConnectionFailed($"Error sending run finished message: {ex.Message}");
                    }
                    finally
                    {
                        // Always clean up resources
                        _sharedWebSocketHelper.Dispose();
                        _sharedWebSocketHelper = null;
                        _runStarted = false;
                    }
                }
            }
        }
    }
}