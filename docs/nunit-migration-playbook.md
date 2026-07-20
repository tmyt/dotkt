# NUnit migration playbook — retiring the per-case bash gate, one family at a time

**Status:** active. This is the step-by-step procedure every `cases/il-*` family follows to move onto the
in-process NUnit suite. It operationalizes `docs/design-nunit-test-harness.md` (the design) under the authority
of `docs/reviews/2026-07-19-cases-test-design-audit.md` (esp. 必須是正条件 **#14**: a later case that subsumes
an earlier one DELETES the old in the SAME change).

The **generics battery** (`tests/il/fixtures/GenericsTests.kt`, migrating `cases/il-generic .. il-generic6`) is
the worked reference — read it alongside this doc.

---

## 0. Foundation (already stood up — reuse it, don't rebuild it)

- **Local-SDK feed.** `make pack` builds the 5 DotKt nupkgs into `build/nuget-feed`. `tests/nuget.config`
  routes every `DotKt.*` package for the test projects to that feed (isolated `globalPackagesFolder` =
  `build/test-package-cache`, cleared `fallbackPackageFolders`) so the suite tests the compiler in THIS working
  tree — not a published nuget. This is why the suite catches current bugs the pilot's published rc7 cannot.
- **Production suite project.** `tests/il/DotKt.Tests.Il.ktproj` — a DotKt `IsTestProject` where migrated
  battery fixtures land, one `.kt` per family under `tests/il/fixtures/`. Resolves the local SDK; version pinned
  to `packaging/DotKt.Versions.props` (`DotKtVersionPrefix` + `-DotKtVersionSuffix`).
- **Gate driver + discovered-count guard.** `tests/run-nunit-il.sh` builds each battery project against the
  local feed, runs `dotnet test` with a TRX logger, and asserts the **discovered test count** equals the
  `EXPECTED` manifest — so a silently dropped fixture/method (or total discovery failure = 0 tests) reddens the
  gate. Then it runs `tests/run-ilverify.sh` once per emitted assembly (baseline `ILVERIFY_XFAIL`).
- **ilverify** stays formal-only, once per assembly (design D2).

---

## 1. Standard assertion imports (use these in EVERY battery)

NUnit's static asserts live on `ClassicAssert.Companion` in DotKt (a C# static class surfaces its statics on the
Kotlin `.Companion`). `import ... as` aliases each as a plain callable, so tests read idiomatically:

```kotlin
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsNull as assertNull
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreNotEqual as assertNotEqual
```

Then: `assertEquals(42, actual)`, `assertTrue(x)`, `assertNull(y)`.

- **Import only the aliases a battery actually uses** (the generics battery imports only `assertEquals`).
- **Verify each name resolves** for your battery; if one does not resolve via `.Companion.`, note it and fall
  back to `ClassicAssert.<Name>(...)` for that call only.
- The even-shorter form without `.Companion.` (`import ...ClassicAssert.AreEqual as assertEquals`) is under
  separate investigation — **do NOT depend on it**; the `.Companion.` form works today (0.9.6-rc7 + local SDK).

### Assertion mapping (design D1 — value asserts are strictly stronger than the old stdout diff)

| Old `cases/il-*` shape | NUnit assertion |
|---|---|
| `println(value)` vs a golden line | `assertEquals(expected, value)` |
| a `null` line | `assertNull(value)` (a wrong non-null can't alias to the string `"null"`) |
| a `true`/`false` line | `assertTrue(x)` / `assertFalse(x)` (can't alias to `"True"`) |
| the contract IS the text (`toString()`, console protocol) | `assertEquals("P(x=1)", p.toString())` — preserves the exact textual check, scoped to one method |

---

## 2. Per-family migration steps

Do ALL of this in **one commit** (audit #14). Work in an isolated worktree; never touch the main tree.

1. **Identify the family.** Group the `cases/il-*` dirs that share plain-Kotlin, oracle, compile conditions, and
   runtime refs into ONE battery (audit §2/§5/§15 — case granularity ≠ execution granularity). Progressive
   milestones (`il-generic..6`), reference/value duplicate pairs (`il-cwindowed`/`il-cwindowedv`), and
   copy-paste dupes collapse to methods in one fixture.

2. **Read EACH case's exact coverage before writing anything.** For every `cases/il-<x>/*.kt` read the source
   AND its expected output. IL cases are registered in `scripts/verify-il.sh` via `il_check <name> <Asm>
   <srcDir> "<expected>"` — the 4th arg is the golden stdout, one line per `println`. **Preserve every asserted
   value 1:1**; annotate each with a `// <expected>` trailing comment so the mapping is auditable.

3. **Build the battery fixture** at `tests/il/fixtures/<Family>Tests.kt`:
   - One `class <Family>Tests` with **one `@TestAttribute fun` per scenario** (name it after the old case).
   - Top-level declarations (classes/functions the scenarios use) go above the fixture class. **Use unique
     top-level names per battery** (one battery = one namespace/assembly; the pilot used `Shape2`/`Rect2` to
     avoid clashes) — a former standalone `main` and another battery must not collide.
   - Convert each golden line to a value assert per §1. `main()` disappears; there is no stdout.
   - A file header comment lists which `cases/il-*` each method replaces (see `GenericsTests.kt`).

4. **Register the count.** Bump the family's project entry in `tests/run-nunit-il.sh` `EXPECTED` by the number
   of `@TestAttribute` methods added (generics = 6). This is the machine-readable governance: the PR shows the
   count delta, and a dropped method reddens the gate.

5. **`dotnet test` GREEN on the LOCAL SDK.** `make pack` (if the compiler changed since the last pack), then
   `bash tests/run-nunit-il.sh`. Require: `discovered == EXPECTED`, all pass, ilverify clean. A new ilverify
   finding is either a real regression (fix it) or a known formal-only one (add to `run-ilverify.sh`
   `ILVERIFY_XFAIL` with a tracking issue, mirroring `verify-il.sh`).

6. **DELETE the old lane in the SAME commit (audit #14):**
   - `git rm -r cases/il-<x> …` for every migrated dir.
   - Remove each `il_check …` line from `scripts/verify-il.sh` (leave a one-line breadcrumb pointing at the new
     fixture, as the generics migration did — state what the code does NOW, don't annotate history).
   - Remove the migrated names from the `PURE=` list in `scripts/verify-differential.sh` (they were the
     redundant CLR-recompile the audit §8 condemns; the JVM oracle applies only where JVM-equivalence is the
     specific claim — design D3 — which value asserts don't need).
   - Watch for **substring traps**: `il-generic` is a prefix of `il-genctor`/`il-genhof` — delete exact tokens.

7. **Verify the bash gate stays green MINUS the family.** `bash -n` every edited script. Run
   `./scripts/verify-il.sh` — the removed cases simply no longer run; the NEW-FAIL/FIXED diff must be **empty**
   (no case you didn't migrate regressed). If the family had `PURE` entries, a quick `verify-differential.sh`
   scan confirms the list still parses.

8. **PR must show the case-count delta (audit #12/#16):** "N `cases/il-*` dirs removed → M `@TestAttribute`
   methods; compiler invocations −N; EXPECTED +M". Coverage is preserved by construction (step 2), so the count
   drop is pure fixed-cost recovery, not lost coverage.

---

## 3. Special lanes (don't force these into the value-assert battery)

- **Round-trip / cross-module** (`verify-roundtrip.sh`): a `<ProjectReference>` producer→consumer pair, to be
  built as a gated lane during the roundtrip/ktproj consolidation (the DLL-not-source invariant: the producer
  is a sibling dir so the consumer glob can't capture its `.kt`).
- **Injected-runtime interop** (a case shipping `runtime.cs`): the C# helper becomes a `<ProjectReference>`'d
  csproj, mechanically identical to the roundtrip producer.
- **Compile-fail / diagnostic-text** cases: stay in a small `kotc`-invoking shell lane (the code must NOT
  compile — it can't be a runtime NUnit test). This is the only irreducible non-NUnit IL lane.
- **Non-deterministic async** (`Task.Delay(1)` cases, audit §10): re-express with a `TaskCompletionSource` +
  explicit barrier so the slow-path (await-before-complete) is forced deterministically — done once, not per
  case.
- **Process-exit / stack-overflow / console-protocol** cases: keep process-isolated in the shell lane so a hard
  crash doesn't take down the rest of the test host's results.

---

## 4. When to shard into a new battery assembly

The audit/design target is ~8–16 battery assemblies of ~25–50 related cases (NUnit's process-isolation boundary
is the **assembly**, not the fixture). Start families as fixtures in `tests/il`; when it grows past ~25–50 cases
or a family needs isolation (its own runtime refs, a crash-sensitive scenario, an interop csproj), split it into
its own `tests/il-<battery>/…` project with its own `EXPECTED` entry. One project = one assembly = one namespace.

---

## 5. Do NOT delete the bash gate wholesale up front

Keep `cases/` and `scripts/verify-*.sh` running until each family is proven migrated; each migration removes only
its own family (design §9). The bash gate reaches retirement when the last family has moved, not before.
