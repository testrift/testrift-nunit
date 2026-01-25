using System;
using System.Threading;
using NUnit.Framework;

namespace ExampleTests
{
    [TestFixture]
    public class MyTestTree
    {
        private Random _random = new Random();

        [TestFixture]
        public class TestSuite1
        {
            private Random _random = new Random();

            [Test]
            public void Test1()
            {
                Console.WriteLine("TestSuite1.Test1 starting");
                Thread.Sleep(_random.Next(100, 500));
                Assert.Pass("TestSuite1.Test1 passed");
            }
        }

        [TestFixture]
        public class TestSuite2
        {
            private Random _random = new Random();

            [Test]
            public void Test1()
            {
                Console.WriteLine("TestSuite2.Test1 starting");
                Thread.Sleep(_random.Next(100, 500));
                Assert.Pass("TestSuite2.Test1 passed");
            }

            [Test]
            public void Test2()
            {
                Console.WriteLine("TestSuite2.Test2 starting");
                Thread.Sleep(_random.Next(100, 500));
                Assert.Pass("TestSuite2.Test2 passed");
            }


            [TestCase("example.com")]
            [TestCase("test.file.log")]
            public void ParameterizedTestWithDots(string input)
            {
                Console.WriteLine($"Testing with input: {input}");
                Assert.That(input, Does.Contain("."), $"Input '{input}' should contain a dot character");
                Assert.Pass($"Parameter test passed with value: {input}");
            }

            [TestFixture]
            public class TestSuite21
            {
                private Random _random = new Random();

                [Test]
                public void Test1()
                {
                    Console.WriteLine("TestSuite2.TestSuite21.Test1 starting");
                    Thread.Sleep(_random.Next(100, 500));
                    Assert.Pass("TestSuite2.TestSuite21.Test1 passed");
                }
            }

            [TestFixture]
            public class TestSuite22
            {
                private Random _random = new Random();

                [Test]
                public void Test1()
                {
                    Console.WriteLine("TestSuite2.TestSuite22.Test1 starting");
                    Thread.Sleep(_random.Next(100, 500));
                    Assert.Pass("TestSuite2.TestSuite22.Test1 passed");
                }

                [Test]
                public void Test2()
                {
                    Console.WriteLine("TestSuite2.TestSuite22.Test2 starting");
                    Thread.Sleep(_random.Next(100, 500));
                    Assert.Pass("TestSuite2.TestSuite22.Test2 passed");
                }
            }
        }

        [TestFixture]
        public class TestSuite3
        {
            private Random _random = new Random();

            [Test]
            public void Test1()
            {
                Console.WriteLine("TestSuite3.Test1 starting");
                Thread.Sleep(_random.Next(100, 500));
                Assert.Pass("TestSuite3.Test1 passed");
            }
        }
    }
}
