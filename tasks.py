"""
Invoke tasks for NUnit plugin.
"""

from invoke import Collection, task
import os
import shutil
from pathlib import Path


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
    repo_dir = Path(__file__).parent
    for p in repo_dir.rglob("bin"):
        if p.is_dir():
            shutil.rmtree(p, ignore_errors=True)
    for p in repo_dir.rglob("obj"):
        if p.is_dir():
            shutil.rmtree(p, ignore_errors=True)
    artifacts = repo_dir / "artifacts"
    if artifacts.exists():
        shutil.rmtree(artifacts, ignore_errors=True)


@task(pre=[clean])
def pack(c, configuration="Release"):
    """Create the NuGet package (nupkg + snupkg) into ./artifacts."""
    repo_dir = Path(__file__).parent
    proj_dir = repo_dir / "src" / "TestRift.NUnit"
    out_dir = repo_dir / "artifacts"
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

    repo_dir = Path(__file__).parent
    artifacts_dir = repo_dir / "artifacts"
    for pkg in artifacts_dir.glob("*.nupkg"):
        if pkg.name.endswith(".snupkg"):
            continue
        c.run(f"dotnet nuget push \"{pkg}\" --api-key \"{api_key}\" --source \"{source}\" --skip-duplicate")


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
