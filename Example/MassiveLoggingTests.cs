using System;
using System.Threading;
using NUnit.Framework;
using TestRift.NUnit;

namespace ExampleTests
{
    /// <summary>
    /// Stress test fixture for massive real-time logging.
    /// Each test generates a high volume of log messages to test WebSocket batching performance.
    /// </summary>
    [TestFixture]
    [Parallelizable(ParallelScope.Children)]
    public class MassiveLoggingTests
    {
        private const int LogsPerTest = 2000;
        private const int DelayBetweenLogsMs = 2;

        [Test]
        public void MassiveLog_Test01() => GenerateMassiveLogs("Test01");

        [Test]
        public void MassiveLog_Test02() => GenerateMassiveLogs("Test02");

        [Test]
        public void MassiveLog_Test03() => GenerateMassiveLogs("Test03");

        [Test]
        public void MassiveLog_Test04() => GenerateMassiveLogs("Test04");

        [Test]
        public void MassiveLog_Test05() => GenerateMassiveLogs("Test05");

        [Test]
        public void MassiveLog_Test06() => GenerateMassiveLogs("Test06");

        [Test]
        public void MassiveLog_Test07() => GenerateMassiveLogs("Test07");

        [Test]
        public void MassiveLog_Test08() => GenerateMassiveLogs("Test08");

        [Test]
        public void MassiveLog_Test09() => GenerateMassiveLogs("Test09");

        [Test]
        public void MassiveLog_Test10() => GenerateMassiveLogs("Test10");

        [Test]
        public void MassiveLog_Test11() => GenerateMassiveLogs("Test11");

        [Test]
        public void MassiveLog_Test12() => GenerateMassiveLogs("Test12");

        [Test]
        public void MassiveLog_Test13() => GenerateMassiveLogs("Test13");

        [Test]
        public void MassiveLog_Test14() => GenerateMassiveLogs("Test14");

        [Test]
        public void MassiveLog_Test15() => GenerateMassiveLogs("Test15");

        [Test]
        public void MassiveLog_Test16() => GenerateMassiveLogs("Test16");

        private void GenerateMassiveLogs(string testName)
        {
            var logger = new TRLog($"Device_{testName}", $"Channel_{testName}");
            var random = new Random();

            Console.WriteLine($"[{testName}] Starting massive logging test with {LogsPerTest} messages");

            for (int i = 0; i < LogsPerTest; i++)
            {
                // Alternate between tx and rx messages
                if (i % 2 == 0)
                {
                    logger.LogTx($"[{i:D4}] Command: AT+TEST={random.Next(1000)}");
                }
                else
                {
                    logger.LogRx($"[{i:D4}] Response: OK DATA={random.Next(10000):X4}");
                }

                // Small delay to simulate real-world message timing
                if (i % 10 == 0)
                {
                    Thread.Sleep(DelayBetweenLogsMs);
                }
            }

            Console.WriteLine($"[{testName}] Completed massive logging test");
            Assert.Pass($"Generated {LogsPerTest} log messages successfully");
        }
    }

    /// <summary>
    /// Additional test fixture with varied message patterns for batching stress testing.
    /// </summary>
    [TestFixture]
    [Parallelizable(ParallelScope.Children)]
    public class MassiveLoggingVariedTests
    {
        [Test]
        public void BurstLogging_100MessagesNoDelay()
        {
            var logger = new TRLog("BurstDevice", "BurstChannel");

            Console.WriteLine("Starting burst logging - 100 messages with no delay");

            for (int i = 0; i < 100; i++)
            {
                logger.Log($"Burst message {i:D3}: timestamp={DateTime.UtcNow:O}");
            }

            Console.WriteLine("Burst logging complete");
            Assert.Pass("Burst logging completed successfully");
        }

        [Test]
        public void LargeMessages_50x1KB()
        {
            var logger = new TRLog("LargeMessageDevice", "LargeChannel");
            var largePayload = new string('X', 1024);

            Console.WriteLine("Starting large message test - 50 x 1KB messages");

            for (int i = 0; i < 50; i++)
            {
                logger.Log($"[{i:D2}] Large payload: {largePayload}");
                Thread.Sleep(5);
            }

            Console.WriteLine("Large message test complete");
            Assert.Pass("Large message test completed successfully");
        }

        [Test]
        public void MixedComponents_MultipleDevices()
        {
            var logger1 = new TRLog("Device1", "COM1");
            var logger2 = new TRLog("Device2", "COM2");
            var logger3 = new TRLog("Device3", "USB0");

            Console.WriteLine("Starting mixed component logging");

            for (int i = 0; i < 100; i++)
            {
                logger1.LogTx($"D1 TX: {i}");
                logger2.LogRx($"D2 RX: {i}");
                logger3.Log($"D3 LOG: {i}");
                Thread.Sleep(1);
            }

            Console.WriteLine("Mixed component logging complete");
            Assert.Pass("Mixed component test completed successfully");
        }

        [Test]
        public void RapidFireWithExceptions()
        {
            var logger = new TRLog("ExceptionDevice", "ErrorChannel");

            Console.WriteLine("Starting rapid fire logging with periodic exceptions");

            for (int i = 0; i < 200; i++)
            {
                logger.Log($"Message {i}");

                // Report an exception every 50 messages
                if (i % 50 == 49)
                {
                    try
                    {
                        throw new InvalidOperationException($"Simulated error at message {i}");
                    }
                    catch (Exception ex)
                    {
                        _ = TestContextWrapper.ReportException(ex, $"Periodic exception at {i}");
                    }
                }
            }

            Console.WriteLine("Rapid fire with exceptions complete");
            Assert.Pass("Rapid fire with exceptions completed successfully");
        }
    }
}
