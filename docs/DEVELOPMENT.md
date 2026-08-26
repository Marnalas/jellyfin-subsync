# Development

## Running the tests

The two halves are tested separately, and both run on every push and pull
request (the `test` and `test-sidecar` jobs in `.github/workflows/build-release.yml`).

**Plugin** - xunit, targeting the pure logic: path mapping, the work builder,
the skip-cache, sweep progress, and the plugin's half of the sidecar protocol.

```sh
dotnet test Jellyfin.Subsync.Starter.Tests/Jellyfin.Subsync.Starter.Tests.csproj
```

**Sidecar** - pytest, targeting job bookkeeping: pruning, the cancel paths, the
worker's claim, timeout clamping and temp-file cleanup.

```sh
cd subsync-sidecar
python -m venv .venv && . .venv/bin/activate
pip install -r requirements-dev.txt
pytest
```

`requirements-dev.txt` deliberately doesn't install `ffsubsync`: the suite puts
a stand-in script on `PATH`, which is what makes the failure, timeout and
missing-binary cases reachable at all, and keeps a full run under two seconds.

The two suites meet at `subsync-sidecar/tests/test_contract.py`, which pins the
response shapes that `SubsyncClientTests.cs` hardcodes as fakes. If a test in
there fails, the matching C# fake needs the same edit.
