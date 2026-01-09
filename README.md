# TestRift.NUnit - NUnit Test Logging with WebSocket Support

**TestRift.NUnit** is the NUnit client library for **TestRift**, a real-time test run server. This library provides real-time logging and run metadata for NUnit tests using an attribute-based approach with WebSocket integration.

## Experimental

TestRift is currently in an **experimental** phase. APIs, configuration, and data formats may change at any time **without notice**.

## Structure

- `src/TestRift.NUnit/` - Contains the `TestRift.NUnit` library source code and project
  - `TestRift.NUnit.csproj` - Project file for the library
  - `*.cs` - Source files

- `Example/` - Example test suite using TestRift.NUnit
  - `ExampleTests.cs` - Sample test class with TRLogger attribute
  - `ExampleTests.csproj` - Project file for example tests
  - `run-tests.bat` - Convenience script to run tests

- `tests/` - Test projects for TestRift.NUnit
  - See [tests/README.md](tests/README.md) for details

## Features

- **Assembly-level logging**: Simply add `[assembly: TRLogger]` to your test project
- **Automatic test run detection**: Uses `[SetUpFixture]` to detect test run start/finish
- **WebSocket integration**: Real-time log streaming to a web server
- **Accurate timestamps**: Timestamps reflect when messages were first written, not when sent
- **Message ordering**: Proper ordering of console output messages
- **Message pooling**: Efficient batching of log messages
- **File logging**: Local log files for each test (saved in bin directory)
- **Console capture**: Captures all Console.WriteLine output
- **Thread-safe**: All operations are thread-safe
- **Stack trace reporting**: Automatically streams NUnit failure stack traces and exposes an API for custom diagnostic traces

## Usage

1. Build the projects:
   ```bash
   cd testrift-nunit/src/TestRift.NUnit
   dotnet build

   cd ../../Example
   dotnet build
   ```

2. Run the example tests:
   ```bash
   cd testrift-nunit/Example
   dotnet test
   ```

3. Or use the convenience script:
   ```bash
   cd testrift-nunit
   .\run-tests.bat
   ```

## Adding to Your Own Tests

1. **Add the TestRift.NUnit project reference** to your test project:
   ```xml
   <ProjectReference Include="..\src\TestRift.NUnit\TestRift.NUnit.csproj" />
   ```

2. **Add the required NuGet packages** to your test project:
   ```xml
   <PackageReference Include="NUnit" Version="3.14.0" />
   <PackageReference Include="NUnit3TestAdapter" Version="4.5.0" />
   <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.0.0" />
   ```

3. **Add the assembly-level attribute and SetUpFixture** to your test project:
   ```csharp
   using TestRift.NUnit; // Add this

   [assembly: TRLogger] // Add this

   namespace ExampleTests
   {
      // Add this
      [SetUpFixture]
      public class MyRunHooks : RunHooks
      {
      }

      [TestFixture]
      public class MyTests
      {
         [Test]
         public void MyTest()
         {
            Console.WriteLine("This will be logged in real-time!");
            Assert.Pass();
         }
      }
   }
   ```

4. **Run your tests** using `dotnet test` - the real-time logging will work automatically!

## Configuration

### Metadata configuration (`TestRiftNUnit.yaml`)

The plugin can optionally load a YAML config file. Discovery order:

- `TESTRIFT_NUNIT_YAML` (filesystem path), otherwise
- `./TestRiftNUnit.yaml` in the current working directory

All string fields support `${env:VAR_NAME}` expansion (missing variables expand to an empty string), which is useful in CI pipelines.

See [docs/config.md](docs/config.md) for the full schema and examples.

## WebSocket Protocol

The NUnit plugin uses the same WebSocket message types and payloads described in
[websocket_protocol.md](../docs/websocket_protocol.md). All messages it sends (`run_started`,
`test_case_started`, `log_batch`, `exception`, `test_case_finished`, `run_finished`) follow
that documented format.

## Exception Reporting

- When a test fails, the framework automatically streams the failure message and exception details to the server.
- You can also report additional exceptions manually, for example inside a `catch` block:

```csharp
try
{
    DoSomethingRisky();
}
catch (Exception ex)
{
    await TestContextWrapper.ReportException(ex);
    Assert.Fail("Recorded exception for investigation.");
}
```

An overload is also available if you want to provide raw strings:

```csharp
await TestContextWrapper.ReportException(
    "Custom failure description",
    myStackTraceString,
    typeof(MyCustomException).FullName);
```

## Log Message API

The `TRLog` class provides a convenient way to send log messages with component, channel, and direction information.

### Instance Usage

Create a `TRLog` instance with a component (and optional channel):

```csharp
using TestRift.NUnit;

// Create a TRLog instance (automatically gets WebSocketHelper)
var logger = new TRLog("Tester5", "COM91");

// Send a message (uses component "Tester5" and channel "COM91")
logger.Log("AT+TEST=1");

// Send with direction using enum
logger.Log("AT+TEST=1", Direction.Tx);
logger.Log("OK", Direction.Rx);

// Convenience methods for direction
logger.LogTx("AT+TEST=1");
logger.LogRx("OK");
```

### Static Method Usage

For one-off messages without creating an instance:

```csharp
TRLog.Log("Tester5", "AT+TEST=1", "COM91", Direction.Tx);
TRLog.Log("Tester5", "OK", "COM91", Direction.Rx);
```

### Direct API (without helper)

You can also use the `WebSocketHelper.QueueLogMessage` method directly:

```csharp
var wsHelper = TestContextWrapper.GetWebSocketHelper();
wsHelper.QueueLogMessage(
    message: "AT+TEST=1",
    component: "Tester5",
    channel: "COM91",
    dir: "tx",
    testCaseId: TestContext.CurrentContext.Test.FullName
);
```

## Testing

See [tests/README.md](tests/README.md) for more details.
