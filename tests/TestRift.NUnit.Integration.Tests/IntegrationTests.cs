using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using TestRift.NUnit;

[assembly: TRLogger]

namespace TestRift.NUnit.Integration.Tests
{
    [SetUpFixture]
    public class TestRunHooks : RunHooks
    {
        // Uses base implementation from RunHooks

        [OneTimeTearDown]
        public void TearDown()
        {
            // Kill the server after all tests complete
            Console.WriteLine("[Integration Tests TearDown] Terminating auto-started server on port 19999...");
            var killed = ServerAutoStarter.KillStartedServer(19999);
            Console.WriteLine(killed
                ? "[Integration Tests TearDown] Server on port 19999 terminated."
                : "[Integration Tests TearDown] No server found on port 19999.");

            // Also call base class teardown
            base.RunFinished();
        }
    }

    [TestFixture]
    public class IntegrationTests
    {
        [Test]
        public void Test_WithLogging_CompletesSuccessfully()
        {
            TRLog.Log("testrift-nunit", "Starting integration test");

            for (int i = 0; i < 3; i++)
            {
                TRLog.Log("testrift-nunit", $"Iteration {i + 1}");
                Thread.Sleep(100);
            }

            TRLog.Log("testrift-nunit", "Integration test completed");
            Assert.Pass();
        }

        [Test]
        public void Test_WithAttachment_CompletesSuccessfully()
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFile, "Integration test attachment content\nLine 2\nLine 3");
                TestContextWrapper.AddTestAttachment(tempFile, "Integration test file");

                TRLog.Log("testrift-nunit", "Attachment added successfully");
                Assert.Pass();
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        [Test]
        public void Test_WithException_ReportsStackTrace()
        {
            TRLog.Log("testrift-nunit", "This test will throw an exception");

            Assert.Throws<InvalidOperationException>(() =>
            {
                throw new InvalidOperationException("Test exception for stack trace verification");
            });
        }

        [Test]
        public void Test_WithMultipleLogCalls_MaintainsOrder()
        {
            for (int i = 1; i <= 10; i++)
            {
                TRLog.Log("testrift-nunit", $"Log entry {i}");
                Console.WriteLine($"Console entry {i}");
            }

            Assert.Pass();
        }
    }
}
