# Process-boundary MSBuild tests

`run.sh` covers build behavior that a single in-process NUnit fixture cannot express. It generates its projects
under `build/tests-msbuild`. Independent project-reference behavior belongs in the NUnit suites.

- **Stateful builds** — two builds against the same `obj/` tree, mutating and deleting source files in between, so
  that stale compiler output cannot be reused.
- **Separate-process program behavior** — for example the compiler-synthesized `suspend fun main` entry point,
  whose fault propagation only shows in a real process exit.
- **Cross-target builds** (`ktproj-crosstarget-rid-assets`) — a `dotnet build -r <rid>` for a RID that differs from
  the host, over the throwaway RID-implementation package built from `rid-probe/`. It asserts that ilemit selects
  the `runtimes/<rid>/lib` asset of the TARGET RID, both on an exact-RID hit and through the RID fallback chain
  (`win-x64` to `win`, `linux-x64` to `unix`), with the portable RID graph and with the built-in chain. Selecting
  the wrong asset is a red build, not a subtly different program: the package's RID-neutral placeholder omits the
  marker member that the RID assets declare, so the emit fails to link it. The scenario also replays the emit at
  the host RID and requires that replay to FAIL, which is what keeps the assertion from passing vacuously.
