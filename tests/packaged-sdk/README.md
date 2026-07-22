# Packaged SDK tests

`run.sh` packs the current SDK into an isolated local NuGet feed, then builds and runs Exe, Library,
multiplatform, CLI-template, and MPP-template scenarios. These are shell scenarios because package restore,
template installation, and isolated NuGet state are the behavior under test.
