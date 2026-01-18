using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace TestRift.NUnit
{
    /// <summary>
    /// Tracks per-test execution state using NUnit's TestContext and detects the transition
    /// from "Inconclusive" to a final status as a proxy for "teardown has started".
    ///
    /// When detected, it can:
    /// - Tag subsequent log entries with phase="teardown"
    /// - Report stack traces only once (shared with TRLoggerAttribute.AfterTest)
    /// </summary>
    internal static class TeardownMonitor
    {
        private sealed class State
        {
            public string LastStatus;
            public bool TeardownStarted;
            public bool ExceptionReported;
        }

        private static readonly ConcurrentDictionary<string, State> _states = new();

        private static bool IsInconclusive(string status) =>
            string.Equals(status, "Inconclusive", StringComparison.OrdinalIgnoreCase);


        public static string GetPhase(string nunitTestId)
        {
            if (string.IsNullOrWhiteSpace(nunitTestId)) return null;
            return _states.TryGetValue(nunitTestId, out var st) && st.TeardownStarted ? "teardown" : null;
        }

        /// <summary>
        /// Call on any activity. Returns the current phase (null or "teardown").
        /// </summary>
        public static string OnActivity(
            string nunitTestId,
            WebSocketHelper webSocketHelper,
            string activity,
            bool aboutToSendLog,
            string timestamp = null)
        {
            if (string.IsNullOrWhiteSpace(nunitTestId))
            {
                return null;
            }

            var st = _states.GetOrAdd(nunitTestId, _ => new State());

            // Read current status
            string currentStatus = null;
            try
            {
                currentStatus = TestContext.CurrentContext?.Result?.Outcome?.Status.ToString();
            }
            catch
            {
                currentStatus = null;
            }

            // Update + detect transition
            var prev = st.LastStatus;
            var statusChanged = false;
            var willStartTeardown = false;
            if (!string.IsNullOrEmpty(currentStatus) && !string.Equals(currentStatus, prev, StringComparison.Ordinal))
            {
                st.LastStatus = currentStatus;
                statusChanged = true;

                // Treat transition away from "Inconclusive" as teardown starting.
                // Also allow triggering if prev is null (we missed the initial Inconclusive snapshot)
                // but current is already non-inconclusive while logs are still being written.
                willStartTeardown =
                    !st.TeardownStarted &&
                    !IsInconclusive(currentStatus) &&
                    (IsInconclusive(prev) || string.IsNullOrEmpty(prev));
            }

            // If teardown has started, ensure we emit marker once (but only when we actually
            // have teardown log activity to show in the UI). We deliberately do NOT
            // send a dedicated websocket marker; we rely on per-log `phase="teardown"`.

            // If we saw a status transition, report the exception (once) BEFORE we flip teardown=true.
            // This avoids the stack trace landing inside the UI's Teardown group (which is order-based).
            if (statusChanged)
            {
                string preferredExceptionTimestamp = null;
                if (willStartTeardown && aboutToSendLog && !string.IsNullOrWhiteSpace(timestamp))
                {
                    // Ensure the exception sorts before the first teardown log even if timestamps collide.
                    if (DateTime.TryParse(
                        timestamp,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var dt))
                    {
                        preferredExceptionTimestamp = dt.AddMilliseconds(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    }
                }

                TryReportExceptionOnce(
                    nunitTestId,
                    st,
                    webSocketHelper,
                    blockUntilSent: willStartTeardown && aboutToSendLog,
                    preferredTimestamp: preferredExceptionTimestamp);
            }

            if (willStartTeardown)
            {
                st.TeardownStarted = true;
            }

            return st.TeardownStarted ? "teardown" : null;
        }

        public static void OnAfterTest(string nunitTestId, WebSocketHelper webSocketHelper = null, string preferredTimestamp = null)
        {
            if (string.IsNullOrWhiteSpace(nunitTestId)) return;
            var st = _states.GetOrAdd(nunitTestId, _ => new State());
            TryReportExceptionOnce(nunitTestId, st, webSocketHelper, blockUntilSent: false, preferredTimestamp: preferredTimestamp);
        }

        private static void TryReportExceptionOnce(
            string nunitTestId,
            State st,
            WebSocketHelper webSocketHelper,
            bool blockUntilSent,
            string preferredTimestamp)
        {
            if (st.ExceptionReported) return;

            try
            {
                var testResult = TestContext.CurrentContext?.Result;
                if (testResult == null) return;

                if (testResult.Outcome.Status == TestStatus.Failed &&
                    !string.IsNullOrWhiteSpace(testResult.StackTrace))
                {
                    var label = testResult.Outcome.Label;
                    var isError = !string.IsNullOrEmpty(label) &&
                                  label.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0;

                    // Mark first to avoid duplicate sends if ReportException triggers more activity.
                    st.ExceptionReported = true;

                    Task sendTask;
                    if (webSocketHelper != null)
                    {
                        sendTask = webSocketHelper.SendExceptionAsync(
                            nunitTestId,
                            testResult.Message,
                            testResult.StackTrace,
                            testResult.Outcome.Status.ToString(),
                            null,
                            isError,
                            preferredTimestamp);
                    }
                    else
                    {
                        sendTask = TestContextWrapper.ReportException(
                            testResult.Message,
                            testResult.StackTrace,
                            testResult.Outcome.Status.ToString(),
                            isError);
                    }

                    if (blockUntilSent)
                    {
                        sendTask.GetAwaiter().GetResult();
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}


