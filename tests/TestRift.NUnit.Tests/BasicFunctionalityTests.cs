using System;
using System.IO;
using NUnit.Framework;
using TestRift.NUnit;

namespace TestRift.NUnit.Tests
{
    [TestFixture]
    public class BasicFunctionalityTests
    {
        [Test]
        public void TRLog_CanSendStaticLogMessage()
        {
            // Arrange & Act - just verify it doesn't throw
            // (without server, messages are queued or dropped)
            TRLog.Log("TestComponent", "Test message");

            // Assert - if we get here, it didn't throw
            Assert.Pass();
        }

        [Test]
        public void TRLog_CanCreateInstanceAndLog()
        {
            // Arrange
            var log = new TRLog("TestComponent", "TestChannel");

            // Act & Assert - verify it doesn't throw
            log.Log("Test message");
            log.LogTx("TX message");
            log.LogRx("RX message");

            Assert.Pass();
        }

        [Test]
        public void TestContextWrapper_AddTestAttachment_DoesNotThrow()
        {
            // Create a temporary test file
            var tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFile, "Test content");

                // Test that AddTestAttachment doesn't throw
                Assert.DoesNotThrow(() =>
                    TestContextWrapper.AddTestAttachment(tempFile, "Test attachment"));
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
        public void TestContextWrapper_ClearAttachments_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => TestContextWrapper.ClearAttachments());
        }
    }
}
