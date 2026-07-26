# Packaged SDK tests

`run.sh` packs the current SDK into an isolated local NuGet feed, then builds and runs Exe, Library,
cross-module asynchronous coroutine, multiplatform, CLI-template, and MPP-template scenarios. The coroutine
scenario builds a packaged Kotlin producer and a separately compiled Kotlin consumer, proves that
`Task.Delay().await()` returns before resuming across the assembly boundary, and IL-verifies both outputs.
These are shell scenarios because package restore, template installation, isolated NuGet state, and separate
compilation are the behavior under test.

All machine-wide state the run would otherwise touch is redirected into the scratch workspace
(`build/verify-packaged-sdk`, wiped at the start of every run): NuGet resolves through a per-run
`nuget.config` with a `<clear/>`ed source list, the local feed only, and a scratch `globalPackagesFolder`;
`dotnet new` runs against a per-run template hive (`--debug:custom-hive`) rather than the store under `$HOME`.
Nothing outside the repository is read or written, so this gate may run concurrently in several worktrees and
needs no cleanup after a failure.

Full-source build and smoke CI for third-party projects are outside this repository's scope. This gate
intentionally keeps only the compiler-facing packaged-SDK/cross-module slice and does not copy external source trees.
