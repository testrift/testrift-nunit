# TestRift.NUnit - NUnit Test Logging with WebSocket Support

**TestRift.NUnit** is the NUnit client library for **TestRift**, a real-time test run server. This library provides real-time logging and run metadata for NUnit tests using an attribute-based approach with WebSocket integration.

## Structure

- `src/TestRift.NUnit/` - Contains the `TestRift.NUnit` library source code and project
  - `TestRift.NUnit.csproj` - Project file for the library
  - `*.cs` - Source files

- `Example/` - Example test suite using TestRift.NUnit
  - `ExampleTests.cs` - Sample test class with TRLogger attribute
  - `ExampleTests.csproj` - Project file for example tests
  - `run-tests.bat` - Convenience script to run tests

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

### Connection settings

Set environment variables to configure the WebSocket connection:
- `TEST_LOG_SERVER_URL` - Server base URL used for WebSocket and HTTP (default: `http://localhost:8080`). The client converts this to `ws://.../ws/nunit` or `wss://.../ws/nunit` internally.
- `TEST_DUT_NAME` - Device name reported as the default `device` field for log entries (default: machine name).

### Metadata configuration (`TRNUnitConfig.yaml`)

You can provide additional run metadata (shown in the server UI and analyzer) via a small YAML file and an environment variable:

- `TRNUNIT_CONFIG_PATH` - Path to a YAML config file (for example `TRNUnitConfig.yaml`). If set, the NUnit plugin loads this file at test run start.

YAML structure:
```yaml
# Human-readable name for this test run (displayed in UI instead of run_id)
runName: Nightly Build #${env:BUILD_NUMBER}

# Optional custom run ID. If set, the server will validate it and use it instead of generating one.
# Supports environment variable expansion with ${env:VAR_NAME} syntax.
# runId: ${env:BUILD_ID}

metadata:
  - name: DUT
    value: ${env:DUT_MODEL}
    url: ${env:DUT_URL}
  - name: Firmware
    value: ${env:FIRMWARE_BRANCH}
  - name: TestSystem
    value: nuts-v2
    url: ${env:TESTSYSTEM_URL}
```

Notes:
- `runName` (optional) provides a human-readable name displayed in the UI instead of the server-generated `run_id`.
- `runId` (optional) allows you to specify a custom run ID. The server validates that it's URL-safe and not already in use. If validation fails, the server returns an error and the test run will not start. If omitted, the server generates a unique run ID automatically.
- Each entry under `metadata` becomes a key in the server's `user_metadata` object, with `value` and optional `url`.
- The plugin expands `${env:VAR_NAME}` placeholders using environment variables at runtime. Missing variables expand to empty strings.
- In the example `run-tests.bat`, `TRNUNIT_CONFIG_PATH` is set to `TRNUnitConfig.yaml` and the YAML file is copied to the test output directory so the plugin can load it.

#### Group metadata (optional)

Add an optional `group` block if you want the server to group runs together (for example by product and branch):

```yaml
group:
  name: ${env:PRODUCT}
  metadata:
    - name: Branch
      value: ${env:BRANCH}
      url: ${env:TESTSYSTEM_URL}/${env:BRANCH}
```

The NUnit plugin sends the group name and metadata (same name/value/url structure as `metadata`) in each `run_started` message. The server computes a deterministic group hash from the name plus metadata values so that the index, analyzer, and matrix pages can show links scoped to that group. See [websocket_protocol.md](../docs/websocket_protocol.md) for the complete message schema.

#### Custom Run ID for CI Integration (optional)

For CI systems such as Jenkins, you can specify a custom `runId` in the YAML config. This allows you to calculate the test run URL **before** starting the tests, which is useful when you need to publish or reference the URL early in your CI pipeline.

```yaml
runId: ${env:BUILD_ID}
```

When using a custom `runId`:
- The test run URL can be calculated as: `{TEST_LOG_SERVER_URL}/testRun/{runId}/index.html`
- This URL is known **before** the test run starts, unlike URL files which require waiting for the run to start
- The server validates that the run ID is URL-safe (allows percent encoding like `%2F` for special characters, but raw slashes are not allowed)
- The server ensures the run ID is unique (checks both active runs and the database)
- If validation fails, the server returns an error and the test run will not start

Example CI usage (Jenkins):
```bash
# Set runId from Jenkins BUILD_ID before running tests
export BUILD_ID="jenkins-${BUILD_NUMBER}"

# Calculate the URL immediately (before tests start)
TEST_RUN_URL="${TEST_LOG_SERVER_URL}/testRun/${BUILD_ID}/index.html"
echo "Test results will be available at: ${TEST_RUN_URL}"

# Run tests (plugin will use the runId from config)
dotnet test
```

#### URL file generation (optional)

For CI integrations, you can configure the plugin to write URL files pointing to the test run and group results. These files are written immediately after the test run starts, allowing CI scripts to discover and publish the URLs.

```yaml
urlFiles:
  # Path to write the test run URL (optional)
  runUrlFile: test_run_url.txt
  # Path to write the group runs URL (optional, only written if run belongs to a group)
  groupUrlFile: test_group_url.txt
```

When configured:
- `runUrlFile`: After the run starts, the plugin writes the full URL to the test run page (e.g., `http://localhost:8080/testRun/abc-123/index.html`) to this file.
- `groupUrlFile`: If the run belongs to a group, the plugin writes the full URL to the group runs page (e.g., `http://localhost:8080/groups/a1b2c3d4`) to this file.

The paths support environment variable expansion using `${env:VAR_NAME}` syntax if needed.

**Note:** Using a custom `runId` (see above) is an alternative to URL files when you need to know the URL before the test run starts. URL files are useful when you want the server to generate the run ID and then read it from the file.

Example CI usage with URL files:
```bash
dotnet test

# After tests start, read the URL files
TEST_RUN_URL=$(cat test_run_url.txt)
echo "Test results: $TEST_RUN_URL"
```

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

## Key Features

- **Accurate Timestamps**: Messages are timestamped when first written to console, not when sent via WebSocket
- **Proper Ordering**: Console output is buffered and sent in the correct order
- **Efficient Batching**: Messages are batched for efficient WebSocket transmission
- **Thread Safety**: All operations are thread-safe for concurrent test execution
- **Automatic Cleanup**: Resources are properly disposed after test completion