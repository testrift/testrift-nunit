using NUnit.Framework;
using TestRift.NUnit;

[assembly: TRLogger]
[assembly: Parallelizable(ParallelScope.All)]
[assembly: LevelOfParallelism(8)]   // choose based on CPU / IO

namespace ExampleTests
{
    [SetUpFixture]
    public class MyRunHooks : RunHooks
    {
    }
}
