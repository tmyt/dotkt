# Packaged SDK tests

`run.sh` packs the current SDK into an isolated local NuGet feed, then builds and runs Exe, Library,
cross-module asynchronous coroutine, multiplatform, CLI-template, and MPP-template scenarios. The coroutine
scenario builds a packaged Kotlin producer and a separately compiled Kotlin consumer, proves that
`Task.Delay().await()` returns before resuming across the assembly boundary, and IL-verifies both outputs.
These are shell scenarios because package restore, template installation, isolated NuGet state, and separate
compilation are the behavior under test.

The state the scenarios themselves create lives in the scratch workspace (`build/verify-packaged-sdk`, wiped at
the start of every run): the SDK-resolution path restores through a per-run `nuget.config` with a `<clear/>`ed
source list, the local feed only, and a scratch `globalPackagesFolder`; each template case installs into a
template hive of its own (`dotnet new --debug:custom-hive`) rather than the store under `$HOME`. So this gate
may run concurrently in several worktrees, and a failed run leaves nothing behind to clean up.

Build-time inputs are deliberately outside that boundary and are not what the gate tests: the refcheck tool and
everything `scripts/pack-nuget.sh` builds (Gradle, the .NET toolchain projects, the packing itself) use the
default NuGet configuration and the user's Gradle home.

Full-source build and smoke CI for third-party projects are outside this repository's scope. This gate
intentionally keeps only the compiler-facing packaged-SDK/cross-module slice and does not copy external source trees.
