using System;
using System.IO;
using NUnit.Framework;

namespace TestRift.NUnit.Tests
{
    [TestFixture]
    public class ServerAutoStarterTests
    {
        private static readonly string TestServerYaml = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestRiftServer.yaml");
        private const string TestServerUrl = "http://localhost:19999";
        private const int TestServerPort = 19999;

        [OneTimeTearDown]
        public void TearDown()
        {
            // Kill the test server we started
            Console.WriteLine($"[TearDown] Terminating server on port {TestServerPort}...");
            var killed = ServerAutoStarter.KillStartedServer(TestServerPort);
            Console.WriteLine(killed 
                ? $"[TearDown] Server on port {TestServerPort} terminated." 
                : $"[TearDown] No server found on port {TestServerPort}.");
        }

        [Test]
        public void EnsureServerRunning_WithInvalidUrl_DoesNotThrow()
        {
            // Test that invalid URLs are handled gracefully
            Assert.DoesNotThrow(() => 
                ServerAutoStarter.EnsureServerRunning("not-a-url"));
        }

        [Test]
        public void EnsureServerRunning_WithHttpsUrl_SkipsAutoStart()
        {
            // Test that HTTPS URLs skip auto-start (only http:// supported)
            Assert.DoesNotThrow(() => 
                ServerAutoStarter.EnsureServerRunning("https://localhost:8080"));
        }

        [Test]
        public void EnsureServerRunning_WithRemoteHost_SkipsAutoStart()
        {
            // Test that remote hosts skip auto-start (only localhost supported)
            Assert.DoesNotThrow(() => 
                ServerAutoStarter.EnsureServerRunning("http://example.com:8080"));
        }

        [Test]
        public void EnsureServerRunning_IsIdempotent()
        {
            // Test that calling multiple times doesn't cause issues
            // Uses unique test port 19999 configured in TestRiftServer.yaml
            // to avoid conflicts with development server on default port 8080
            Assert.DoesNotThrow(() =>
            {
                ServerAutoStarter.EnsureServerRunning(TestServerUrl, TestServerYaml);
                ServerAutoStarter.EnsureServerRunning(TestServerUrl, TestServerYaml);
            });
        }
    }
}
