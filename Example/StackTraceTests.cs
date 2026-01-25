using System;
using System.Threading;
using NUnit.Framework;
using TestRift.NUnit;

namespace ExampleTests
{
    [TestFixture]
    public class StackTraceTests
    {
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
    }
}
