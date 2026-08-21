using NUnit.Framework;

namespace TestRift.NUnit.Tests
{
    [TestFixture]
    public class SystemMetricsCollectorTests
    {
        [Test]
        public void TryParseProcStatTimes_ComputesIdleKernelAndUser()
        {
            const string procStat =
                "cpu  100 20 30 400 50 5 10 2 0 0\n" +
                "cpu0 50 10 15 200 25 2 5 1 0 0\n";

            Assert.IsTrue(SystemMetricsCollector.TryParseProcStatTimes(
                procStat, out var idle, out var kernel, out var user));

            // idle = idle + iowait = 400 + 50
            Assert.AreEqual(450, idle);
            // kernel = system + irq + softirq + steal + idle = 30+5+10+2+450
            Assert.AreEqual(497, kernel);
            // user = user + nice = 100 + 20
            Assert.AreEqual(120, user);

            var busy = (kernel + user) - idle;
            var total = kernel + user;
            Assert.AreEqual(167, busy);
            Assert.AreEqual(617, total);
        }

        [Test]
        public void TryParseProcMemInfo_PrefersMemAvailable()
        {
            const string memInfo =
                "MemTotal:        8000000 kB\n" +
                "MemFree:         1000000 kB\n" +
                "MemAvailable:    3000000 kB\n" +
                "Buffers:          200000 kB\n" +
                "Cached:          1500000 kB\n";

            Assert.IsTrue(SystemMetricsCollector.TryParseProcMemInfo(
                memInfo, out var total, out var available));

            Assert.AreEqual(8000000L * 1024, total);
            Assert.AreEqual(3000000L * 1024, available);
        }

        [Test]
        public void TryParseProcMemInfo_FallsBackWithoutMemAvailable()
        {
            const string memInfo =
                "MemTotal:        8000000 kB\n" +
                "MemFree:         1000000 kB\n" +
                "Buffers:          200000 kB\n" +
                "Cached:          1500000 kB\n";

            Assert.IsTrue(SystemMetricsCollector.TryParseProcMemInfo(
                memInfo, out var total, out var available));

            Assert.AreEqual(8000000L * 1024, total);
            Assert.AreEqual(2700000L * 1024, available);
        }
    }
}
