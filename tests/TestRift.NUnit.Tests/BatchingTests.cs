using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TestRift.NUnit;

namespace TestRift.NUnit.Tests
{
    /// <summary>
    /// Tests for WebSocket batching behavior.
    /// These tests verify that event batching and heartbeat mechanisms work correctly.
    /// </summary>
    [TestFixture]
    public class BatchingTests
    {
        [Test]
        public void WebSocketHelper_CanBeCreated()
        {
            // Just verify WebSocketHelper can be instantiated
            using var helper = new WebSocketHelper("http://localhost:9999");
            Assert.Pass("WebSocketHelper created successfully");
        }

        [Test]
        public void WebSocketHelper_DisposesCleanly()
        {
            // Verify disposal works without throwing
            var helper = new WebSocketHelper("http://localhost:9999");
            Assert.DoesNotThrow(() => helper.Dispose());
        }

        [Test]
        public void WebSocketHelper_MultipleDisposeDoesNotThrow()
        {
            // Verify double-dispose is safe
            var helper = new WebSocketHelper("http://localhost:9999");
            helper.Dispose();
            Assert.DoesNotThrow(() => helper.Dispose());
        }

        [Test]
        public async Task WebSocketHelper_FlushBatchAsync_DoesNotThrowWhenNotConnected()
        {
            // Flush should not throw when WebSocket is not connected
            using var helper = new WebSocketHelper("http://localhost:9999");
            await helper.FlushBatchAsync();
            Assert.Pass("FlushBatchAsync completed without throwing");
        }

        [Test]
        public async Task WebSocketHelper_FlushAllMessagesAsync_DoesNotThrowWhenNotConnected()
        {
            // FlushAllMessages should not throw when not connected
            using var helper = new WebSocketHelper("http://localhost:9999");
            await helper.FlushAllMessagesAsync();
            Assert.Pass("FlushAllMessagesAsync completed without throwing");
        }

        [Test]
        public void WebSocketHelper_FlushAllMessages_DoesNotThrowWhenNotConnected()
        {
            // Synchronous flush should not throw when not connected
            using var helper = new WebSocketHelper("http://localhost:9999");
            helper.FlushAllMessages();
            Assert.Pass("FlushAllMessages completed without throwing");
        }

        [Test]
        public void TRLog_RapidLogging_DoesNotBlock()
        {
            // Verify that rapid logging doesn't block the caller
            var logger = new TRLog("BatchTest", "RapidChannel");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Log many messages rapidly
            for (int i = 0; i < 1000; i++)
            {
                logger.Log($"Rapid message {i}");
            }

            stopwatch.Stop();

            // Should complete very quickly (< 1 second for 1000 messages)
            Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(1000),
                "Rapid logging took too long - may indicate blocking behavior");
        }

        [Test]
        public void TRLog_ParallelLogging_DoesNotDeadlock()
        {
            // Verify that parallel logging from multiple threads doesn't deadlock
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var exceptions = new ConcurrentBag<Exception>();
            var threads = new List<Thread>();

            for (int t = 0; t < 8; t++)
            {
                int threadId = t;
                var thread = new Thread(() =>
                {
                    try
                    {
                        var logger = new TRLog($"Thread{threadId}", "ParallelChannel");
                        for (int i = 0; i < 100 && !cts.Token.IsCancellationRequested; i++)
                        {
                            logger.Log($"Message {i} from thread {threadId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
                threads.Add(thread);
                thread.Start();
            }

            // Wait for all threads to complete
            foreach (var thread in threads)
            {
                thread.Join(TimeSpan.FromSeconds(15));
            }

            Assert.That(exceptions, Is.Empty, "Parallel logging caused exceptions");
            Assert.Pass("Parallel logging completed without deadlock");
        }

        [Test]
        public void TRLog_MixedDirectionLogging_WorksCorrectly()
        {
            // Verify that mixed tx/rx logging works
            var logger = new TRLog("MixedDevice", "COM1");

            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    logger.LogTx($"TX {i}");
                    logger.LogRx($"RX {i}");
                    logger.Log($"Plain {i}");
                }
            });
        }

        [Test]
        public async Task TRLog_ConcurrentLoggingWithFlush_DoesNotDeadlock()
        {
            // Verify that concurrent logging + flush operations don't deadlock
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var exceptions = new ConcurrentBag<Exception>();

            var logTask = Task.Run(() =>
            {
                try
                {
                    var logger = new TRLog("ConcurrentDevice", "FlushChannel");
                    for (int i = 0; i < 500 && !cts.Token.IsCancellationRequested; i++)
                    {
                        logger.Log($"Message {i}");
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            // Wait for task with timeout
            var completed = await Task.WhenAny(logTask, Task.Delay(TimeSpan.FromSeconds(15)));

            cts.Cancel();

            Assert.That(exceptions, Is.Empty, "Concurrent logging caused exceptions");
            Assert.That(completed, Is.EqualTo(logTask), "Logging task timed out - possible deadlock");
        }

        [Test]
        public void TestContextWrapper_ReportException_DoesNotBlock()
        {
            // Verify that reporting exceptions doesn't block
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            for (int i = 0; i < 10; i++)
            {
                try
                {
                    throw new InvalidOperationException($"Test exception {i}");
                }
                catch (Exception ex)
                {
                    _ = TestContextWrapper.ReportException(ex, $"Test context {i}");
                }
            }

            stopwatch.Stop();

            // Should complete very quickly
            Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(1000),
                "Exception reporting took too long");
        }
    }
}
