# Packaged SDK tests

`run.sh` packs the current SDK into an isolated local NuGet feed, then builds and runs Exe, Library,
cross-module asynchronous coroutine, multiplatform, CLI-template, and MPP-template scenarios. The coroutine
scenario builds a packaged Kotlin producer and a separately compiled Kotlin consumer, proves that
`Task.Delay().await()` returns before resuming across the assembly boundary, and IL-verifies both outputs.
These are shell scenarios because package restore, template installation, isolated NuGet state, and separate
compilation are the behavior under test.

The state the scenarios create stays inside the repository, all of it gitignored: the scratch workspace
`build/verify-packaged-sdk` and the local feed `build/nuget-feed`, both wiped at the start of every run, plus
the cached refcheck tool in `build/verify-packaged-sdk-tool`, which is kept between runs on purpose. Within
that boundary the SDK-resolution path restores through a per-run `nuget.config` with a `<clear/>`ed source
list, the local feed only, and a scratch `globalPackagesFolder`, and each template case installs into a
template hive of its own (`dotnet new --debug:custom-hive`) rather than the store under `$HOME`. A failed run
leaves nothing behind to clean up.

Building the toolchain the scenarios then exercise is deliberately outside that boundary: `scripts/pack-nuget.sh`
drives Gradle (Maven Central and `~/.gradle`) and the toolchain's own NuGet restore, and the refcheck tool
restores from the user's configured NuGet sources — both into `~/.nuget/packages`, whose extraction is
lock-protected and safe for concurrent worktrees. `~/.gradle` is the one shared resource with a history of
cross-worktree contention; `gradle.properties` carries the mitigation and names the next lever if it recurs.
Two more reaches outside the repository, neither of them shared mutable state: `run-ilverify.sh` locates
ILVerify under `$HOME/.dotnet` (a read; the ILVerify check itself is part of the coroutine scenario's verdict),
and `verify-pack-idempotency.sh` re-runs the pack twice more, using a `mktemp -d` scratch it removes on exit.

Full-source build and smoke CI for third-party projects are outside this repository's scope. This gate
intentionally keeps only the compiler-facing packaged-SDK/cross-module slice and does not copy external source trees.
