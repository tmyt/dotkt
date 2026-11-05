# Packaged SDK tests

`make verify-packaged-sdk` runs `run.sh`: it packs the current SDK into a local NuGet feed, checks
the packaged tool versions, and builds and runs Exe, Library, C# consumer, multi-target reference,
cross-module asynchronous coroutine, multiplatform, CLI-template, and MPP-template scenarios. The coroutine
scenario builds a packaged Kotlin producer and a separately compiled Kotlin consumer, proves that
`Task.Delay().await()` returns before resuming across the assembly boundary, and IL-verifies both outputs.
These are shell scenarios because package restore, template installation, isolated NuGet state, and separate
compilation are the behavior under test.

The application-manifest scenario cross-builds `win-x64` Kotlin and same-target C# applications and reads
`RT_MANIFEST/1/0` from the final native `.exe` as well as the managed DLL (ID 2 for Library). It compares the full manifest XML and exact DotKt payload bytes, including a BOM,
checks build/publish, content changes, selection changes, clearing, true no-op builds and missing/unusable
files, and verifies that manifest edits do not recompile Kotlin. It also compares native resource IDs and
payloads byte-for-byte against a same-identity C# control for default/disabled manifests, Exe/WinExe/Library,
and file/product version changes, including the final build/publish apphost and no-apphost output.
This scenario maps only DotKt packages to
the local feed; the SDK-selected `Microsoft.NETCore.App.Host.*` pack may come from nuget.org in its isolated
cache. Explicit `.res` inputs are checked against the selected SDK's Roslyn; COFF is not a supported
input. The test inspects Windows PE files on the build host; it does not execute Windows UI or validate OS policy.

The managed-version scenario compares the actual Assembly table and version attributes with a same-target
C# control: SDK defaults and explicit overrides, Version prefix/suffix, SourceRevisionId composition,
generation opt-outs, quoting, wildcard expansion, no-op/incremental changes, Library/publish output and
separately compiled Kotlin/C# consumers with nonzero AssemblyRef versions. The manifest scenario also
compares managed versions alongside the native resources, keeping these distinct surfaces coherent.

The JIT-debugging scenario checks Debug/Release defaults, explicit `Optimize` overrides and independent
symbol settings against actual assembly `DebuggableAttribute` metadata and same-target C# controls.
It verifies target-BCL identity, rejects unintended Edit and Continue/symbol-store flags, runs the Kotlin
outputs, and checks no-op builds plus emission-only invalidation when optimization changes.

The Portable PDB scenario checks Kotlin documents and ordered sequence points, the DLL's matching
CodeView identity, and symbol selection, incremental emission, publish and clean. This is a binary
and runtime test, not an interactive debugger test. The reference-assembly scenario checks the
opt-in declaration-only DLL, signature and Kotlin metadata preservation, and separate C# and Kotlin
project-reference consumers.

The state the scenarios create stays inside the repository, all of it gitignored: the scratch workspace
`build/verify-packaged-sdk` and the local feed `build/nuget-feed`, both wiped at the start of every run, plus
the cached refcheck tool in `build/verify-packaged-sdk-tool`, which is kept between runs on purpose. Within
that boundary the DotKt SDK-resolution path restores through a per-run `nuget.config` with a `<clear/>`ed source
list, the local feed only (except the separately mapped apphost pack described above), and a scratch `globalPackagesFolder`, and each template case installs into a
template hive of its own (`dotnet new --debug:custom-hive`) rather than the machine-wide store.
The scratch workspace is retained for diagnosis and replaced on the next run.

Building the toolchain the scenarios then exercise is deliberately outside that boundary: `scripts/pack-nuget.sh`
drives Gradle (Maven Central and `~/.gradle`) and the toolchain's own NuGet restore, and the refcheck tool
restores from the user's configured NuGet sources — both into `~/.nuget/packages`, whose extraction is
lock-protected and safe for concurrent worktrees. `~/.gradle` is the one shared resource with a history of
cross-worktree contention; `gradle.properties` carries the mitigation and names the next lever if it recurs.
Before packing, `test_stdlib_build.py` exercises the real stdlib/pack scripts in temporary repositories with
controlled compiler processes. A failed stage must stop subsequent stages, stamping and packaging even if it
leaves partial outputs; successful builds must retain fingerprint-based reuse. It can also be run standalone
with `python3 tests/packaged-sdk/test_stdlib_build.py` without building the toolchain.
Two more reaches outside the repository, neither of them shared mutable state: `run-ilverify.sh` locates
ILVerify under `$HOME/.dotnet` (a read; the ILVerify check itself is part of the coroutine scenario's verdict),
and `verify-pack-idempotency.sh` checks standalone and Make pack reuse and content invalidation
using a `mktemp -d` scratch it removes on exit.

Full-source build and smoke CI for third-party projects are outside this repository's scope. This gate
intentionally keeps only the compiler-facing packaged-SDK/cross-module slice and does not copy external source trees.
