# TestRift.NUnit Tests

This directory contains tests for the TestRift.NUnit library.

## Test Projects

### TestRift.NUnit.Tests
- **Purpose**: Unit tests for basic functionality
- **Dependencies**: Uses **minimum supported versions** (NUnit 3.7.0)
- **Tests**:
  - Basic logging functionality (`TRLog`)
  - Test context wrapper (`TestContextWrapper`)
  - Attachment handling
  - Server auto-starter edge cases

### TestRift.NUnit.Tests.MaxVersions
- **Purpose**: Compatibility tests with maximum supported versions
- **Dependencies**: Uses **latest compatible versions** (NUnit 3.14.*, YamlDotNet 14.*, etc.)
- **Tests**: Shares test code with `TestRift.NUnit.Tests` via linked files
- **Goal**: Ensure the library works with both old and new dependency versions

### TestRift.NUnit.Integration.Tests
- **Purpose**: End-to-end integration tests
- **Features**:
  - Uses `[assembly: TRLogger]` attribute
  - Tests with RunHooks for test run lifecycle
  - Real logging and attachment scenarios
  - Tests order maintenance and exception handling

## Running Tests

### All tests
```bash
inv test.all
```

### Individual test projects
```bash
# Minimum versions
inv test.min-versions

# Maximum versions
inv test.max-versions

# Integration tests
inv test.integration

# Example tests
inv test.example
```

### Alternative (using dotnet directly)
```bash
# Minimum versions
dotnet test tests/TestRift.NUnit.Tests

# Maximum versions
dotnet test tests/TestRift.NUnit.Tests.MaxVersions

# Integration tests
dotnet test tests/TestRift.NUnit.Integration.Tests
```

### With coverage (optional)
```bash
dotnet test --collect:"XPlat Code Coverage"
```

