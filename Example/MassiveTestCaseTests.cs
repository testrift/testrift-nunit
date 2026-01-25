using System;
using System.Threading;
using NUnit.Framework;

namespace ExampleTests
{
    /// <summary>
    /// Stress test fixture for massive test case start/end throughput.
    /// Contains many short tests that start and finish rapidly to test
    /// WebSocket batching of test_case_started and test_case_finished events.
    /// </summary>
    [TestFixture]
    [Parallelizable(ParallelScope.Children)]
    public class MassiveTestCaseStartEndTests
    {
        private static readonly Random Random = new Random();

        // 50 quick tests that start and finish rapidly
        [Test] public void QuickTC_001() => QuickTest(1);
        [Test] public void QuickTC_002() => QuickTest(2);
        [Test] public void QuickTC_003() => QuickTest(3);
        [Test] public void QuickTC_004() => QuickTest(4);
        [Test] public void QuickTC_005() => QuickTest(5);
        [Test] public void QuickTC_006() => QuickTest(6);
        [Test] public void QuickTC_007() => QuickTest(7);
        [Test] public void QuickTC_008() => QuickTest(8);
        [Test] public void QuickTC_009() => QuickTest(9);
        [Test] public void QuickTC_010() => QuickTest(10);

        [Test] public void QuickTC_011() => QuickTest(11);
        [Test] public void QuickTC_012() => QuickTest(12);
        [Test] public void QuickTC_013() => QuickTest(13);
        [Test] public void QuickTC_014() => QuickTest(14);
        [Test] public void QuickTC_015() => QuickTest(15);
        [Test] public void QuickTC_016() => QuickTest(16);
        [Test] public void QuickTC_017() => QuickTest(17);
        [Test] public void QuickTC_018() => QuickTest(18);
        [Test] public void QuickTC_019() => QuickTest(19);
        [Test] public void QuickTC_020() => QuickTest(20);

        [Test] public void QuickTC_021() => QuickTest(21);
        [Test] public void QuickTC_022() => QuickTest(22);
        [Test] public void QuickTC_023() => QuickTest(23);
        [Test] public void QuickTC_024() => QuickTest(24);
        [Test] public void QuickTC_025() => QuickTest(25);
        [Test] public void QuickTC_026() => QuickTest(26);
        [Test] public void QuickTC_027() => QuickTest(27);
        [Test] public void QuickTC_028() => QuickTest(28);
        [Test] public void QuickTC_029() => QuickTest(29);
        [Test] public void QuickTC_030() => QuickTest(30);

        [Test] public void QuickTC_031() => QuickTest(31);
        [Test] public void QuickTC_032() => QuickTest(32);
        [Test] public void QuickTC_033() => QuickTest(33);
        [Test] public void QuickTC_034() => QuickTest(34);
        [Test] public void QuickTC_035() => QuickTest(35);
        [Test] public void QuickTC_036() => QuickTest(36);
        [Test] public void QuickTC_037() => QuickTest(37);
        [Test] public void QuickTC_038() => QuickTest(38);
        [Test] public void QuickTC_039() => QuickTest(39);
        [Test] public void QuickTC_040() => QuickTest(40);

        [Test] public void QuickTC_041() => QuickTest(41);
        [Test] public void QuickTC_042() => QuickTest(42);
        [Test] public void QuickTC_043() => QuickTest(43);
        [Test] public void QuickTC_044() => QuickTest(44);
        [Test] public void QuickTC_045() => QuickTest(45);
        [Test] public void QuickTC_046() => QuickTest(46);
        [Test] public void QuickTC_047() => QuickTest(47);
        [Test] public void QuickTC_048() => QuickTest(48);
        [Test] public void QuickTC_049() => QuickTest(49);
        [Test] public void QuickTC_050() => QuickTest(50);

        private void QuickTest(int testNumber)
        {
            // Log a single message
            Console.WriteLine($"QuickTC {testNumber:D3} executing");

            // Small random delay (10-50ms) to simulate minimal work
            Thread.Sleep(Random.Next(10, 50));

            Assert.Pass($"QuickTC {testNumber:D3} passed");
        }
    }

    /// <summary>
    /// Additional batch of quick tests for even higher TC throughput testing.
    /// </summary>
    [TestFixture]
    [Parallelizable(ParallelScope.Children)]
    public class MassiveTestCaseBatchTwo
    {
        [Test] public void BatchTwo_001() => MiniTest(1);
        [Test] public void BatchTwo_002() => MiniTest(2);
        [Test] public void BatchTwo_003() => MiniTest(3);
        [Test] public void BatchTwo_004() => MiniTest(4);
        [Test] public void BatchTwo_005() => MiniTest(5);
        [Test] public void BatchTwo_006() => MiniTest(6);
        [Test] public void BatchTwo_007() => MiniTest(7);
        [Test] public void BatchTwo_008() => MiniTest(8);
        [Test] public void BatchTwo_009() => MiniTest(9);
        [Test] public void BatchTwo_010() => MiniTest(10);

        [Test] public void BatchTwo_011() => MiniTest(11);
        [Test] public void BatchTwo_012() => MiniTest(12);
        [Test] public void BatchTwo_013() => MiniTest(13);
        [Test] public void BatchTwo_014() => MiniTest(14);
        [Test] public void BatchTwo_015() => MiniTest(15);
        [Test] public void BatchTwo_016() => MiniTest(16);
        [Test] public void BatchTwo_017() => MiniTest(17);
        [Test] public void BatchTwo_018() => MiniTest(18);
        [Test] public void BatchTwo_019() => MiniTest(19);
        [Test] public void BatchTwo_020() => MiniTest(20);

        [Test] public void BatchTwo_021() => MiniTest(21);
        [Test] public void BatchTwo_022() => MiniTest(22);
        [Test] public void BatchTwo_023() => MiniTest(23);
        [Test] public void BatchTwo_024() => MiniTest(24);
        [Test] public void BatchTwo_025() => MiniTest(25);
        [Test] public void BatchTwo_026() => MiniTest(26);
        [Test] public void BatchTwo_027() => MiniTest(27);
        [Test] public void BatchTwo_028() => MiniTest(28);
        [Test] public void BatchTwo_029() => MiniTest(29);
        [Test] public void BatchTwo_030() => MiniTest(30);

        private void MiniTest(int n)
        {
            Console.WriteLine($"BatchTwo mini test {n}");
            Assert.Pass();
        }
    }

    /// <summary>
    /// Mixed test outcomes for stress testing status handling.
    /// </summary>
    [TestFixture]
    [Parallelizable(ParallelScope.Children)]
    public class MassiveTestCaseMixedOutcomes
    {
        [Test] public void Mixed_Pass_01() => Assert.Pass();
        [Test] public void Mixed_Pass_02() => Assert.Pass();
        [Test] public void Mixed_Pass_03() => Assert.Pass();
        [Test] public void Mixed_Pass_04() => Assert.Pass();
        [Test] public void Mixed_Pass_05() => Assert.Pass();

        [Test] public void Mixed_Fail_01() => Assert.Fail("Intentional failure 1");
        [Test] public void Mixed_Fail_02() => Assert.Fail("Intentional failure 2");
        [Test] public void Mixed_Fail_03() => Assert.Fail("Intentional failure 3");

        [Test, Ignore("Skipped for testing")] public void Mixed_Skip_01() => Assert.Pass();
        [Test, Ignore("Skipped for testing")] public void Mixed_Skip_02() => Assert.Pass();

        [Test] public void Mixed_PassWithLog_01()
        {
            Console.WriteLine("Pass with log 1");
            Assert.Pass();
        }

        [Test] public void Mixed_PassWithLog_02()
        {
            Console.WriteLine("Pass with log 2");
            Assert.Pass();
        }

        [Test] public void Mixed_FailWithException()
        {
            Console.WriteLine("About to throw");
            throw new InvalidOperationException("Intentional exception for testing");
        }
    }
}
