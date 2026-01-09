"""
Invoke tasks for NUnit plugin.
"""

from invoke import Collection, task
import os
import shutil
from pathlib import Path
import sys
import importlib.util

# Import test tasks from tests/tasks.py using explicit path
tests_tasks_path = Path(__file__).parent / "tests" / "tasks.py"
spec = importlib.util.spec_from_file_location("test_tasks_module", tests_tasks_path)
test_tasks_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(test_tasks_module)

REPO_DIR = Path(__file__).parent
PROJECTS = [
    REPO_DIR / "Example" / "ExampleTests.csproj",
    REPO_DIR / "tests" / "TestRift.NUnit.Tests" / "TestRift.NUnit.Tests.csproj",
    REPO_DIR / "tests" / "TestRift.NUnit.Tests.MaxVersions" / "TestRift.NUnit.Tests.MaxVersions.csproj",
    REPO_DIR / "tests" / "TestRift.NUnit.Integration.Tests" / "TestRift.NUnit.Integration.Tests.csproj",
]


@task
def build(c):
    """Build the TestRift.NUnit library and Example tests."""
    realtimelog_dir = Path(__file__).parent / "src" / "TestRift.NUnit"
    example_dir = Path(__file__).parent / "Example"

    print("Building TestRift.NUnit library...")
    with c.cd(str(realtimelog_dir)):
        c.run("dotnet build")

    print("Building Example tests...")
    with c.cd(str(example_dir)):
        c.run("dotnet build")


@task
def clean(c):
    """Remove build artifacts (bin/, obj/, artifacts/)."""
    for p in REPO_DIR.rglob("bin"):
        if p.is_dir():
            shutil.rmtree(p, ignore_errors=True)
    for p in REPO_DIR.rglob("obj"):
        if p.is_dir():
            shutil.rmtree(p, ignore_errors=True)
    artifacts = REPO_DIR / "artifacts"
    if artifacts.exists():
        shutil.rmtree(artifacts, ignore_errors=True)


@task(pre=[clean])
def pack(c, configuration="Release"):
    """Create the NuGet package (nupkg + snupkg) into ./artifacts."""
    proj_dir = REPO_DIR / "src" / "TestRift.NUnit"
    out_dir = REPO_DIR / "artifacts"
    out_dir.mkdir(exist_ok=True)
    with c.cd(str(proj_dir)):
        c.run(f"dotnet pack -c {configuration} -o ..\\..\\artifacts")


@task(pre=[pack])
def publish(c, source="https://api.nuget.org/v3/index.json", api_key=None):
    """Publish ./artifacts/*.nupkg to a NuGet source.

    Args:
        source: NuGet v3 feed URL (default: nuget.org).
        api_key: API key. If omitted, uses NUGET_API_KEY env var.
    """
    if api_key is None:
        api_key = os.getenv("NUGET_API_KEY")
    if not api_key:
        raise RuntimeError("Missing NuGet API key. Pass --api-key or set NUGET_API_KEY.")

    artifacts_dir = REPO_DIR / "artifacts"
    for pkg in artifacts_dir.glob("*.nupkg"):
        if pkg.name.endswith(".snupkg"):
            continue
        c.run(f"dotnet nuget push \"{pkg}\" --api-key \"{api_key}\" --source \"{source}\" --skip-duplicate")


@task
def restore_local(c):
    """Restore all test projects and example using local testrift-server NuGet package."""
    server_nuget_dir = REPO_DIR.parent / "testrift-server" / "nuget" / "TestRift.Server" / "bin" / "Release"

    if not server_nuget_dir.exists():
        print(f"ERROR: Local TestRift.Server package not found at: {server_nuget_dir}")
        print("Run 'inv build-nuget' in testrift-server directory first.")
        return

    print(f"Using local TestRift.Server feed: {server_nuget_dir}")
    for proj in PROJECTS:
        print(f"\nRestoring {proj.parent.name}...")
        c.run(f'dotnet restore "{proj}" --force')


@task
def restore(c):
    """Restore all projects using only public NuGet feeds."""
    print("Restoring projects from public NuGet feeds...")
    for proj in PROJECTS:
        print(f"\nRestoring {proj.parent.name} (public)...")
        c.run(f'dotnet restore "{proj}" --force /p:LocalTestRiftServerFeed=__disable__')


@task
def run_example(c, filter=None, server_url="http://localhost:8080"):
    """Run the example NUnit tests.

    Args:
        filter: Optional test filter (e.g., "LogMessageWithComponentAndChannel")
        server_url: Server URL (default: http://localhost:8080)
    """
    example_dir = Path(__file__).parent / "Example"

    env = {
        "TEST_LOG_SERVER_URL": server_url,
        "TEST_DUT_NAME": "INVOKE-TEST",
    }

    cmd = "dotnet test --logger console;verbosity=normal"
    if filter:
        cmd += f" --filter {filter}"

    print(f"Running example tests (server: {server_url})...")
    with c.cd(str(example_dir)):
        c.run(cmd, env=env)


# Configure namespaces
ns = Collection()
ns.add_task(build)
ns.add_task(clean)
ns.add_task(pack)
ns.add_task(publish)
ns.add_task(restore_local)
ns.add_task(restore)
ns.add_task(run_example)

# Add test namespace from tests/tasks.py
ns.add_collection(Collection.from_module(test_tasks_module, name='test'))
