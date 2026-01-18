using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

namespace TestRift.NUnit
{
    /// <summary>
    /// Wrapper for TestContext that tracks attachments
    /// </summary>
    public static class TestContextWrapper
    {
        private static readonly List<TestAttachment> _trackedAttachments = new List<TestAttachment>();
        private static readonly List<Task> _pendingUploads = new List<Task>();
        private static readonly object _lock = new object();
        private static WebSocketHelper _webSocketHelper = null;

        /// <summary>
        /// Get the WebSocket helper instance (if available).
        /// Returns null if not yet initialized.
        /// </summary>
        public static WebSocketHelper GetWebSocketHelper()
        {
            return _webSocketHelper;
        }

        /// <summary>
        /// Set the WebSocket helper for uploads and start any pending uploads
        /// </summary>
        public static void SetWebSocketHelper(WebSocketHelper webSocketHelper)
        {
            _webSocketHelper = webSocketHelper;

            // Start any pending uploads that were tracked earlier
            List<TestAttachment> pendingAttachments;
            lock (_lock)
            {
                pendingAttachments = new List<TestAttachment>(_trackedAttachments);
                _trackedAttachments.Clear();
            }

            if (pendingAttachments.Count > 0 && _webSocketHelper != null)
            {
                foreach (var attachment in pendingAttachments)
                {
                    // Use NUnit test.ID for reliable lookup (TestAdapter uses ID, not Id)
                    var nunitTestId = TestContext.CurrentContext.Test.ID;

                    lock (_lock)
                    {
                        var uploadTask = _webSocketHelper.UploadAttachmentAsync(nunitTestId, attachment.FilePath, attachment.Description);
                        _pendingUploads.Add(uploadTask);
                    }
                }
            }
        }

        /// <summary>
        /// Add a test attachment and start upload immediately
        /// </summary>
        public static void AddTestAttachment(string filePath, string description = null)
        {
            // Call the original NUnit method
            TestContext.AddTestAttachment(filePath, description);

            // Start upload immediately if WebSocket helper is available
            if (_webSocketHelper != null)
            {
                // Use NUnit test.ID for reliable lookup (TestAdapter uses ID, not Id)
                var nunitTestId = TestContext.CurrentContext.Test.ID;

                lock (_lock)
                {
                    var uploadTask = _webSocketHelper.UploadAttachmentAsync(nunitTestId, filePath, description);
                    _pendingUploads.Add(uploadTask);
                }
            }
            else
            {
                // Track it for later upload if no WebSocket helper
                lock (_lock)
                {
                    _trackedAttachments.Add(new TestAttachment
                    {
                        FilePath = filePath,
                        Description = description,
                        TestCaseId = null // Will be set when processing
                    });
                }
            }
        }

        /// <summary>
        /// Report an exception for the current test case using an Exception instance.
        /// </summary>
        public static Task ReportException(Exception exception, string messageOverride = null, bool isError = false)
        {
            if (exception == null)
            {
                return Task.CompletedTask;
            }

            var message = messageOverride ?? exception.Message;
            var stackTrace = exception.StackTrace ?? string.Empty;
            var exceptionType = exception.GetType().FullName;
            return ReportException(message, stackTrace, exceptionType, isError);
        }

        /// <summary>
        /// Report an exception for the current test case using raw string data.
        /// </summary>
        public static Task ReportException(string message, string stackTrace, string exceptionType = null, bool isError = false)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
            {
                return Task.CompletedTask;
            }

            if (_webSocketHelper == null)
            {
                return Task.CompletedTask;
            }

            // Use NUnit test.ID for reliable lookup (TestAdapter uses ID, not Id)
            var nunitTestId = TestContext.CurrentContext.Test.ID;

            lock (_lock)
            {
                var task = _webSocketHelper.SendExceptionAsync(nunitTestId, message, stackTrace, exceptionType, null, isError);
                _pendingUploads.Add(task);
                return task;
            }
        }

        /// <summary>
        /// Get all tracked attachments and clear the list
        /// </summary>
        public static List<TestAttachment> GetAndClearAttachments()
        {
            lock (_lock)
            {
                var attachments = new List<TestAttachment>(_trackedAttachments);
                _trackedAttachments.Clear();
                return attachments;
            }
        }

        /// <summary>
        /// Get all tracked attachments without clearing the list
        /// </summary>
        public static List<TestAttachment> GetAttachments()
        {
            lock (_lock)
            {
                return new List<TestAttachment>(_trackedAttachments);
            }
        }

        /// <summary>
        /// Clear all tracked attachments without returning them
        /// </summary>
        public static void ClearAttachments()
        {
            lock (_lock)
            {
                _trackedAttachments.Clear();
            }
        }

        /// <summary>
        /// Wait for all pending uploads to complete
        /// </summary>
        public static void WaitForUploadsToComplete()
        {
            List<Task> uploadsToWait;
            lock (_lock)
            {
                uploadsToWait = new List<Task>(_pendingUploads);
            }

            if (uploadsToWait.Count > 0)
            {
                Task.WaitAll(uploadsToWait.ToArray());
                lock (_lock)
                {
                    _pendingUploads.RemoveAll(task => task.IsCompleted);
                }
            }
        }
    }
}
