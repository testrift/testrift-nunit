using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TestRift.NUnit;

[assembly: TRLogger]

namespace ExampleTests
{
    [SetUpFixture]
    public class MyRunHooks : RunHooks
    {
    }

    [TestFixture]
    public class ExampleTests
    {
        [Test]
        public void QuickPass()
        {
            Console.WriteLine("Console log from QuickPass()");
            Assert.That(2 + 2, Is.EqualTo(4));
        }

        [Test]
        public void SlowLog()
        {
            for (int i = 0; i < 10; i++)
            {
                Console.WriteLine($"Tick {i}");
                Thread.Sleep(1000);
            }
            Assert.Pass("Finished logging slowly");
        }

        [Test]
        public void Fails()
        {
            Console.WriteLine("About to fail");
            Thread.Sleep(5000);
            Assert.Fail("Boom!");
        }

        [Test]
        public void LogMessageWithComponentAndChannel()
        {
            // Create a TRLog instance (automatically gets WebSocketHelper)
            var logger = new TRLog("Tester5", "COM91");

            // Send messages with direction
            logger.LogTx("AT+TEST=1");
            logger.LogRx("OK");

            // Send message without direction
            logger.Log("Status check");

            // Send message with explicit direction
            logger.Log("AT+TEST=2", Direction.Tx);

            // Use static method for one-off messages
            TRLog.Log("Tester5", "AT+TEST=3", "COM92", Direction.Tx);
            TRLog.Log("Tester5", "OK", "COM92", Direction.Rx);

            Assert.Pass("Logged messages with component and channel");
        }

        [Test]
        public void ManualStackTrace()
        {
            try
            {
                ThrowCustomError();
            }
            catch (Exception ex)
            {
                _ = TestContextWrapper.ReportException(ex, "Manually reported stack trace");
                Assert.Fail("Manual stack trace test failed after reporting stack trace");
            }
        }

        private static void ThrowCustomError()
        {
            throw new InvalidOperationException("Custom failure to demonstrate stack trace reporting");
        }

        [Test]
        public void DeepStackTrace()
        {
            Console.WriteLine("Testing deep stack trace with multiple levels");
            try
            {
                Level1();
            }
            catch (Exception ex)
            {
                _ = TestContextWrapper.ReportException(ex, "Deep nested exception");
                Assert.Fail("Deep stack trace test failed");
            }
        }

        private void Level1()
        {
            Console.WriteLine("Level 1");
            Level2();
        }

        private void Level2()
        {
            Console.WriteLine("Level 2");
            Level3();
        }

        private void Level3()
        {
            Console.WriteLine("Level 3");
            Level4();
        }

        private void Level4()
        {
            Console.WriteLine("Level 4");
            throw new ArgumentException("Exception thrown from deep level 4");
        }

        [Test]
        public void MultipleStackTraces()
        {
            Console.WriteLine("Testing multiple stack trace reports in one test");

            // First exception
            try
            {
                ThrowCustomError();
            }
            catch (Exception ex)
            {
                _ = TestContextWrapper.ReportException(ex, "First exception caught");
            }

            // Second exception with deeper stack
            try
            {
                Level1();
            }
            catch (Exception ex)
            {
                _ = TestContextWrapper.ReportException(ex, "Second exception with deeper stack");
            }

            // Third exception
            try
            {
                throw new InvalidCastException("Third exception for testing");
            }
            catch (Exception ex)
            {
                _ = TestContextWrapper.ReportException(ex, "Third exception");
            }

            Assert.Fail("Test completed with multiple stack traces");
        }

        [Test]
        public void UncaughtException()
        {
            Console.WriteLine("Testing uncaught exception - this should be trapped by NUnit");
            Thread.Sleep(1000);
            throw new InvalidOperationException("This is an uncaught exception that should be trapped by NUnit and reported");
        }

        [Test]
        public void TestWithMultipleOutputs()
        {
            Console.WriteLine("Starting test with multiple outputs");
            Console.WriteLine("This is line 1");
            Console.WriteLine("This is line 2");
            Console.WriteLine("This is line 3");
            Console.WriteLine("Ending test");
            Assert.Pass("Test completed successfully");
        }

        [Test]
        public void TestWithAttachments()
        {
            // Create temporary files for testing attachments
            var tempDir = Path.Combine(Path.GetTempPath(), $"test_attachments_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var tempFile = Path.Combine(tempDir, "test_attachment.txt");
                File.WriteAllText(tempFile, "This is a test attachment file created during test execution.\nTimestamp: " + DateTime.Now);

                var logFile = Path.Combine(tempDir, $"test_log_{Guid.NewGuid()}.txt");
                File.WriteAllText(logFile, $"Test log content\nTest: {TestContext.CurrentContext.Test.Name}\nTime: {DateTime.Now}");

                // Add the files as attachments using our wrapper
                TestContextWrapper.AddTestAttachment(tempFile, "Test attachment file");
                TestContextWrapper.AddTestAttachment(logFile, "Test execution log");

                Assert.Pass("Test completed with attachments");
            }
            finally
            {
                // Wait for all uploads to complete before cleaning up
                TestContextWrapper.WaitForUploadsToComplete();

                // Clean up temporary directory after upload is complete
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error cleaning up temp directory: {ex.Message}");
                }
            }
        }
    }
}
