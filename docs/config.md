## TestRift.NUnit configuration (`TestRiftNUnit.yaml`)

`TestRift.NUnit` reads an optional YAML config file to configure:

- Auto-starting the server for local runs (`autoStartServer`)
- Server connection (`serverUrl`)
- Run naming (`runName`, optional `runId`)
- Run metadata (`metadata`)
- Grouping (`group`)
- Optional URL file generation for CI (`urlFiles`)

### How the config file is discovered

At test run start, the plugin loads config from the first match below:

- **1)** `TESTRIFT_NUNIT_YAML` (filesystem path to the YAML file)
- **2)** `./TestRiftNUnit.yaml` in the **current working directory**

If no config file is found, the plugin uses defaults (server URL defaults to `http://localhost:8080`).

### Environment variable expansion

All string fields support `${env:VAR_NAME}` expansion. Missing variables expand to an empty string.

Example:

```yaml
runName: CI run ${env:GITHUB_RUN_NUMBER}
```

### Schema

#### `serverUrl` (optional)

Base URL to the TestRift server (used for WebSocket + HTTP).

Example: `http://localhost:8080`

#### `autoStartServer` (optional)

Configuration block for auto-starting TestRift Server automatically.

- This only runs when a YAML config file is loaded (i.e. `TestRiftNUnit.yaml` exists or `TESTRIFT_NUNIT_YAML` is set).
- This only supports **local** server URLs (`http://localhost:...`, `http://127.0.0.1:...`, `http://[::1]:...`).
- It assumes the server is installed via pip and available as the `testrift-server` command on `PATH`.
- When enabled, the plugin always attempts to start `testrift-server` (even if `/health` is already OK) so that server-side config mismatch checks are enforced.
- The server compares configs by their **effective values** (config hash), not by filename/path. If your YAML only overrides defaults, it may still be considered identical to the running server’s config.

Example:

```yaml
autoStartServer:
  enabled: true
  serverYaml: ./testrift_server.yaml
  restartOnConfigChange: false
serverUrl: http://localhost:8080
```

Fields:

##### `autoStartServer.enabled` (optional)

If `true`, enables auto-start.

##### `autoStartServer.serverYaml` (optional)

If set, this path will be passed to the server via the `TESTRIFT_SERVER_YAML` environment variable when starting it.

- Supports `${env:VAR_NAME}` expansion.
- The path can be relative; it will be interpreted relative to the directory containing `TestRiftNUnit.yaml`.
- If set and the file does not exist, the test run will fail fast with an error.
- If you run via `dotnet test` and your `TestRiftNUnit.yaml` is copied to the test output directory (`bin/...`), make sure the server YAML is copied there too (or use an absolute path).
- Windows note: avoid YAML double-quoted paths like `"C:\test\TestRiftServer.yaml"` (because `\t` becomes a TAB). Prefer:
  - `serverYaml: C:\test\TestRiftServer.yaml`, or
  - `serverYaml: 'C:\test\TestRiftServer.yaml'` (single quotes), or
  - `serverYaml: C:/test/TestRiftServer.yaml` (forward slashes), or
  - `serverYaml: "C:\\test\\TestRiftServer.yaml"` (escaped backslashes)

##### `autoStartServer.restartOnConfigChange` (optional)

If `true`, starts the server with `--restart-on-config`. If a server is already running on the port with a different config, it will be shut down and restarted with the new config.

#### `runName` (optional)

Human-readable name shown in the UI.

#### `runId` (optional)

Custom run ID sent in `run_started`.

- The server validates that the run ID is URL-safe and not already in use.
- If the server rejects the run ID, the test run is aborted (the plugin treats it as a startup error).
- This is useful in CI when you want a predictable URL.

Given:

- `serverUrl: http://localhost:8080`
- `runId: my-run-123`

the run URL will be:

- `http://localhost:8080/testRun/my-run-123/index.html`

#### `metadata` (optional)

List of name/value/url entries shown in the UI:

```yaml
metadata:
  - name: Firmware
    value: ${env:FIRMWARE_BRANCH}
    url: https://example.com/builds/${env:BUILD_ID}
```

#### `group` (optional)

Groups runs together in the UI (group pages, analyzer, matrix):

```yaml
group:
  name: ${env:PRODUCT}
  metadata:
    - name: Branch
      value: ${env:BRANCH}
```

When `group` is set, the server computes a deterministic group hash. The UI exposes group-scoped pages such as:

- `http://localhost:8080/groups/<group-hash>`
- `http://localhost:8080/analyzer?group=<group-hash>`
- `http://localhost:8080/matrix?group=<group-hash>`

#### `urlFiles` (optional)

Write URLs to files after the run starts (handy in CI):

```yaml
urlFiles:
  runUrlFile: test_run_url.txt
  groupUrlFile: test_group_url.txt
```

Behavior:

- Files are written after the server replies to `run_started` (so the final `run_id` and group hash are known).
- Paths support `${env:VAR_NAME}` expansion.
- `runUrlFile` is written if configured.
- `groupUrlFile` is written only if the run belongs to a group (server returns `group_url`).
- The file contents are the full absolute URL, built from `serverUrl` plus the relative URL returned by the server.

This is useful in CI to publish a link to results without parsing logs.

### Protocol mapping

This YAML config influences fields in the `run_started` message:

- `runName` → `run_name`
- `runId` → `run_id` (optional)
- `metadata` → `user_metadata`
- `group` → `group`

For the complete wire format, see [`websocket_protocol.md`](../../testrift-server/docs/websocket_protocol.md).

### Full example

```yaml
serverUrl: http://localhost:8080
runName: CI run ${env:GITHUB_RUN_NUMBER}
runId: ${env:GITHUB_RUN_ID}

metadata:
  - name: Firmware
    value: ${env:FIRMWARE_BRANCH}

group:
  name: ${env:PRODUCT}
  metadata:
    - name: Branch
      value: ${env:BRANCH}

urlFiles:
  runUrlFile: test_run_url.txt
  groupUrlFile: test_group_url.txt
```


