using System;
using System.IO;
using NUnit.Framework;
using TestRift.NUnit;

namespace TestRift.NUnit
{
    [SetUpFixture]
    public class RunHooks
    {
        public RunHooks() : this("")
        {
        }

        public RunHooks(string configPath)
        {
            Console.WriteLine("=== RunHooks CONSTRUCTOR CALLED ===");
            ThreadSafeFileLogger.LogRunHooksConstructor();

            // Use constructor parameter first, fall back to environment variable if null or empty
            var finalConfigPath = string.IsNullOrEmpty(configPath)
                ? Environment.GetEnvironmentVariable("TRNUNIT_CONFIG_PATH")
                : configPath;
            if (!string.IsNullOrEmpty(finalConfigPath))
            {
                ConfigManager.Load(finalConfigPath);
                Console.WriteLine($"Config path would be: {finalConfigPath}");
            }
        }

        [OneTimeSetUp]
        public void RunStarted()
        {
            Console.WriteLine("=== TEST RUN STARTED ===");
            ThreadSafeFileLogger.LogRunHooksStarted();
            // Tell TRLogger that test run has started
            TRLoggerAttribute.NotifyRunStarted();
        }

        [OneTimeTearDown]
        public void RunFinished()
        {
            Console.WriteLine("=== TEST RUN FINISHED ===");
            ThreadSafeFileLogger.LogRunHooksFinished();
            // Tell TRLogger that test run has finished
            TRLoggerAttribute.NotifyRunFinished();
        }
    }
}
