"""
Test tasks for TestRift.NUnit
"""

try:
    from invoke import task, Collection
except ImportError as e:
    raise RuntimeError(
        "Invoke is required to run these tasks. Install it with: pip install invoke"
    ) from e

import sys
import os

# Enable ANSI colors on Windows
if sys.platform == "win32":
    os.system("")  # This enables ANSI escape sequences on Windows 10+

# ANSI color codes
class Colors:
    GREEN = '\033[92m'
    RED = '\033[91m'
    YELLOW = '\033[93m'
    BLUE = '\033[94m'
    BOLD = '\033[1m'
    RESET = '\033[0m'


@task(name="min-versions")
def min_versions(c):
    """Run tests with minimum supported package versions (NUnit 3.7.0)."""
    print("\n" + "=" * 60)
    print("Running Tests with Minimum Supported Versions")
    print("=" * 60)
    c.run("dotnet test tests/TestRift.NUnit.Tests/TestRift.NUnit.Tests.csproj --configuration Release --verbosity normal")


@task(name="max-versions")
def max_versions(c):
    """Run tests with maximum supported package versions (NUnit 3.14.x)."""
    print("\n" + "=" * 60)
    print("Running Tests with Maximum Supported Versions")
    print("=" * 60)
    c.run("dotnet test tests/TestRift.NUnit.Tests.MaxVersions/TestRift.NUnit.Tests.MaxVersions.csproj --configuration Release --verbosity normal")


@task
def integration(c):
    """Run integration tests."""
    print("\n" + "=" * 60)
    print("Running Integration Tests")
    print("=" * 60)
    c.run("dotnet test tests/TestRift.NUnit.Integration.Tests/TestRift.NUnit.Integration.Tests.csproj --configuration Release --verbosity normal")

@task
def all(c):
    """Run all test suites (unit, integration, version compatibility)."""
    # Track results for each test suite
    results = {}
    test_suites = [
        ("Minimum Versions", "dotnet test tests/TestRift.NUnit.Tests/TestRift.NUnit.Tests.csproj --configuration Release --verbosity normal"),
        ("Maximum Versions", "dotnet test tests/TestRift.NUnit.Tests.MaxVersions/TestRift.NUnit.Tests.MaxVersions.csproj --configuration Release --verbosity normal"),
        ("Integration Tests", "dotnet test tests/TestRift.NUnit.Integration.Tests/TestRift.NUnit.Integration.Tests.csproj --configuration Release --verbosity normal"),
    ]

    for name, command in test_suites:
        print("\n" + "=" * 60)
        print(f"Running {name}")
        print("=" * 60)
        try:
            result = c.run(command, warn=True)
            results[name] = result.ok
        except Exception as e:
            print(f"{Colors.RED}Error running {name}: {e}{Colors.RESET}")
            results[name] = False

    # Print summary
    print("\n" + "=" * 60)
    print(f"{Colors.BOLD}TEST SUMMARY{Colors.RESET}")
    print("=" * 60)

    all_passed = True
    for name, passed in results.items():
        status = f"{Colors.GREEN}✓ PASSED{Colors.RESET}" if passed else f"{Colors.RED}✗ FAILED{Colors.RESET}"
        print(f"{name:25} {status}")
        if not passed:
            all_passed = False

    print("=" * 60)
    if all_passed:
        print(f"{Colors.GREEN}{Colors.BOLD}All Tests Passed! ✓{Colors.RESET}")
    else:
        print(f"{Colors.RED}{Colors.BOLD}Some Tests Failed! ✗{Colors.RESET}")
        sys.exit(1)
    print("=" * 60)


# Configure test namespace
ns = Collection()
ns.add_task(min_versions)
ns.add_task(max_versions)
ns.add_task(integration)
ns.add_task(all)
