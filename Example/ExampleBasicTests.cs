using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using TestRift.NUnit;

namespace ExampleTests
{
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
