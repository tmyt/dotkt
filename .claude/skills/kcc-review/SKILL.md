---
name: kcc-review
description: Run a review of the KCC (Kotlin CLR Compiler) toolchain + stdlib as review-team leader. Fixed phase order (baseline → issue re-verification → static layer-purity → empirical behavioral → coverage audit → IL quality → synthesis), per-layer specialist subagents + Codex, repo-specific invariants and false-positive traps. Use when asked to review the toolchain, audit layer purity, hunt miscompiles, or certify that the gates are genuinely green.
argument-hint: "[scope: full | static | behavioral | <layer> | <topic>]"
---

# KCC toolchain review — leader's operating procedure

You are the **review-team leader**. Your job is NOT to find bugs yourself first — it is to
orchestrate specialist finders, then act as the **reducer and verifier**: nothing enters the final
report that you have not independently confirmed (by grep/read at current HEAD, or by running a
repro). Subagent and Codex output are *inputs*, never conclusions.

A review is an **assessment deliverable**: report findings, do not fix them (unless the user
explicitly asked for fixes in the same request). The report is written in **Japanese**; everything
else — your reasoning, subagent prompts, repro code — stays in English (CLAUDE.md ground rule).

## Scope calibration

| User asked for | Run phases |
|---|---|
| full / thorough review, "レビューして" with no qualifier | 0–6 (all) |
| layer purity / design / static review | 0, 1, 2, 6 |
| behavioral / correctness / miscompile hunt | 0, 1, 3, 4, 6 |
| certify the gates / "本当に緑か" | 0, 1, 4, 6 |
| single layer or topic | 0, then the matching slice, 6 |

Never skip Phase 0 or Phase 6. Never report a behavioral claim without Phase-3 evidence.

## The mental model (what "correct" means here)

The authoritative layer table and invariants are `docs/architecture.md`. The review-relevant core:

| Layer | Reads | Owns | Must NOT contain |
|---|---|---|---|
| dll2klib | CLR reference dll | CLR declarations → metadata-only KLIB | call binding or CLR physical lowering |
| kotc | stdlib KLIB + reference KLIBs | source → FIR → BIR, symbol resolution | **any CLR/BCL knowledge** (owners, slots, or physical type tokens) |
| bir2cir | stdlib.ref.dll | BIR → CIR; inline/type-substitute/suspend lowering; consumes `@ClrIntrinsic`/`@ClrTypeAlias`/`@ClrProperty` | passing `@ClrIntrinsic` (or any intrinsic label) into CIR |
| ilemit | stdlib.rt.dll | CIR → CIL, ilverify-clean | **any Kotlin knowledge** |
| stdlib (`libraries/stdlib/`) | — | pure-Kotlin `kotlin.*` + `@Clr*` bindings | compiler special-casing on its behalf |

Binding invariants every finding is judged against:
1. `@ClrIntrinsic`: sourced from ref.dll → consumed by bir2cir → **never reaches ilemit**.
2. `kotlin.*` and referenced CLR declarations come from frontend **KLIBs**.
3. The cardinal rule: a stdlib problem is fixed **stdlib-side**, never by a compiler
   special-case/denylist/stub. A kotc hardcode that shadows a working stdlib actual is itself a bug.
4. NO compat shims / dual-track paths — legacy code kept "just in case" is a finding, not a courtesy.
5. Every finding must name the **owning layer** of the violated knowledge. Layer placement is a
   lookup (CLAUDE.md), never an open question in the report.

## Fixed phase order

### Phase 0 — Baseline (mandatory, before any code reading)

1. Record HEAD commit + date. **All file:line citations in the report are pinned to this commit.**
2. `git status` — note dirty build artifacts (`dotkt-out/` churn is known noise, not a finding
   unless the tracking itself is in scope).
3. Build the stdlib through its canonical scripts before trusting cached artifacts:
   `./scripts/build-stdlib-klib.sh`, `./scripts/build-stdlib-ref.sh --emit`, and
   `./scripts/build-stdlib-rt.sh --emit`.
4. **Run gates solo and quiescent.** Use `make verify` for the full gate and the focused runners
   under `tests/` when narrowing a failure. Capture complete output.
5. Read test configuration and expected-failure data from the current test projects and runners;
   do not rely on prose counts from old reviews.
6. Load prior state from open GitHub Issues. An already-tracked item is reported as
   **"known-open (tracked)"**, never as a new finding.

### Phase 1 — Re-verify prior findings

Every finding from the previous review gets a status: **still-present / fixed / changed /
not-reverified** — with fresh evidence (re-grep or re-run; line numbers drift). Do not carry a
prior finding forward on faith, and do not let a fixed one silently vanish: the report's
"修正済みと再確認" table exists for fairness and calibration. In-code comments count as claims to
re-verify, not evidence — a stale comment has already misled one review (the `toString(radix)`
"stdlib miscompiles" comment was wrong; the stdlib actual was correct and the kotc special-case
was the bug).

### Phase 2 — Static passes (per-layer, parallel)

Launch the layer specialists (`dll2klib`, `kotc`, `bir2cir`, `ilemit`, `stdlib` subagent types)
in parallel, each with the subagent contract below. Four sub-passes each:

- **Layer purity**: does the layer hold only its own knowledge? kotc: grep for BCL names,
  `System.`, `get_`/`set_`/`add_`/`remove_` slot construction, `clr:`/`clrg:` emission. ilemit:
  grep for `kotlin.` names and Kotlin-semantics branching. dll2klib: does it limit itself to
  declaration projection, leaving CLR call binding and physical lowering downstream?
- **Failure posture**: classify every fallback/degradation site **loud vs silent**. Silent-wrong
  (degrade to `Any?`, ungated reflection dispatch, swallowed transform miss) outranks loud-crash
  at equal frequency. The make-it-loud fix is part of the finding.
- **Dead code**: a CIR/BIR case is dead only after a **producer check across the upstream layers**
  (grep kotc + bir2cir emit sites; confirm zero producers). Dead code is a real finding here — it
  misrepresents the true CIR surface and violates the no-compat rule.
- **Staleness**: comments/docs vs code. A doc-code contradiction is a finding (which side is wrong
  must be established empirically, not assumed).

### Phase 3 — Empirical behavioral pass (the ship-quality core)

Compile and RUN minimal `.kt` samples via `./scripts/dotkt.sh --run` (options: `--exe`,
`--no-stdlib`, `--retarget`, `--ref <dll>`). The oracle is **real Kotlin/JVM semantics** — run
`kotlinc` if available, otherwise cite documented Kotlin semantics — never DotKt's own output.

- **Check `docs/dotkt-semantics.md` and `docs/user/` first**: a documented, intentional deviation
  is NOT a bug (but a doc that contradicts observed behavior is).
- **Sweep the known-fragile families first** (where past miscompiles clustered): nullable
  primitives + smart-cast unwrap; boxed primitives through generic `T`/`V` (Map get/getOrPut,
  comparators, groupBy, arrays of `Int?`); cross-module and non-constant default arguments;
  `toString` of nested structures (Pair/Triple/data-class containing collections); `hashCode`
  determinism; integer edge cases (`MIN_VALUE` div/rem/abs, radix conversions, `-0.0`); operator
  conventions on stdlib types (`list + list`); extension properties cross-module; suspend
  boundaries. Then broaden.
- Reduce every repro to minimal before reporting; record **actual vs expected** verbatim.
- Also record what was **swept and found CORRECT** — the reassurance section prevents the next
  review from re-burning the same ground and calibrates severity.

### Phase 4 — Coverage audit (why was the gate green anyway?)

For every confirmed Phase-3 bug, name the gate that should have caught it. Audit structurally:

- **The self-scoring trap (COV1 pattern)**: samples validated against fixed strings *captured from
  DotKt's own output* are self-consistent but Kotlin-wrong-forever. Count how many samples the JVM
  oracle (`PURE` list in verify-differential) actually covers vs the total; flag the gap.
- Features marked ✅ in docs with **zero test cases**; dead/unwired fixtures; XFAIL-comment
  contradictions inside the gate scripts.
- Each confirmed bug gets a "needs a regression case" note with the target gate.

### Phase 5 — IL quality (correctness-neutral, kept separate)

Disassemble emitted dlls with `ilspycmd -il` (and `ilverify` for formal checks). Look for:
allocation in loops (uncached non-capturing delegates), boxing at string-template/concat sites,
invariant-generic dispatch shapes, redundant conversions. **Always classified separately from
correctness** and graded against a concrete comparison point (what Roslyn or kotlinc/JVM emits for
the same idiom) — never "this IL looks bad" without a referent.

### Phase 6 — Synthesis and report (leader-only)

1. **Dedup by root-cause family, not symptom** (precedent: ~15 miscompiles collapsed into 2
   families — boxed-primitive dual representation, cross-module default args). A family is one
   top-level finding with its members enumerated.
2. **Verify every surviving finding yourself** at current HEAD before it enters the report.
3. Assign each finding its owning layer + concrete fix direction, and order the recommended fixes
   (gate-neutral quick wins vs design-required medium-term), noting which can proceed in parallel.
4. Severity by **demonstrated impact**, not reviewer surprise. Behavioral findings (user's correct
   Kotlin misbehaves) outrank purity findings of equal apparent grade — ship quality first.

| Grade | Meaning |
|---|---|
| **CRITICAL** | correct Kotlin silently produces wrong results / data loss / memory corruption on a common idiom |
| **HIGH** | crash or wrong result on a common idiom; ABI-fidelity hole in the public surface; layer violation likely to cause wrong-code |
| **MED** | real bug with a narrow trigger; silent-degradation posture; dead-code load; coverage blind spot |
| **LOW** | staleness, hygiene, diagnostics quality, minor IL inefficiency |

## Team & delegation

Use the project layer agents (`dll2klib`, `kotc`, `bir2cir`, `ilemit`, `stdlib`) for Phase 2, and
`general-purpose` agents for the cross-cutting roles (behavioral sweeper, coverage auditor,
IL-quality) in Phases 3–5. Launch independent agents in parallel. Every subagent prompt (English)
must include:

1. **Exact scope boundary** (directories/files) + the rule: *if the root cause is in a sibling
   layer, report the layer + precise symptom; do not cross and do not patch*.
2. **Required evidence format**: claim / file:line / repro command + actual vs expected (if
   behavioral) / oracle used / confidence / suspected false-positive risk. Findings without
   evidence are returned as "unverified concerns", clearly separated.
3. **Instruction to USE Codex** — canonical invocation (the `</dev/null` is MANDATORY, it hangs
   forever otherwise):
   `codex exec -s read-only --skip-git-repo-check "<question in English>" </dev/null`
   If Codex goes silent across agents, it is likely blocked on an interactive self-update prompt
   on the user's terminal — tell the user, and fall back to empirical verification meanwhile.
4. Treat Codex as a **finder, not an oracle**: every Codex claim gets the same re-verification as
   a subagent claim.

## Known false-positive traps (each has already burned one review — check before reporting)

- **stdlib `TODO()` is filler, not a backlog.** The `@kotlin.clr.ClrIntrinsic` annotation is the
  discriminator: a bound member keeps a filler `TODO()` body that is never emitted.
  `grep TODO | wc` is a false metric.
- **Reference-KLIB interop in kotc is LEGIT**: `get_Item`/`set_Item` indexers (gated on
  `clrInteropName != null`), .NET event `add_`/`remove_` accessors, numeric `toInt`/`toLong` →
  `conv`, primitive `bin`/`un`/`ceq`. Codex has over-reported all of these as kotc layer
  violations; they are the .NET-interop surface, not `kotlin.*` leakage.
- **Genuine primitive IL ops stay compiler-lowered** — that is the architecture, not a violation.
- **`kotlin.clr.*` compiler-intrinsic surface** (e.g. `ClrRef` → `byref:`) is boundary-adjacent by
  design; grade it Low/contextual, not as a hard violation.
- **A green gate is weak evidence of correctness** (self-scored fixed strings — see Phase 4); a
  red gate under concurrent builds is weak evidence of breakage (see Phase 0.4).
- **Documented deviations** in `docs/dotkt-semantics.md` are design, not bugs.
- **An open GitHub issue** is known-open, not a new finding.

## Report format (fixed — write in Japanese)

Return the report to the user. Persist it under `build/reports/` only when a file deliverable is
useful; do not add point-in-time review reports to `docs/`.

```markdown
# KCC レビュー報告（YYYY-MM-DD, <scope>）
> レビュー体制（リーダー＋各レイヤーエージェント＋codex）／手法／基準 commit `<sha>`

## 総評（結論）        ← 1段落で健全性の判定と「1つだけ直すなら」
## 重大度サマリ        ← ID | 重大度 | 所見 | レイヤー | 種別 の表（バケット数と個別件数を明記）
## 所見（重大度順・重複排除済み）
   ### <ID> [<重大度>] <タイトル>
   - 場所: file:line（基準 commit 時点）
   - 裏付け: どのエージェント/codex/再現実行
   - 内容 / 発火条件（behavioral は actual vs expected を実測で）
   - 是正: 担当レイヤー + 修正方向（make-it-loud を含む）
   - ゲート: 検出できたはずのゲートと回帰ケースの要否
## 前回レビューから修正済みと再確認   ← 公正のための表（項目 | 現状 | 確認箇所）
## 誤検知として棄却した指摘           ← 何を・なぜ棄却したか（次回の再燃防止）
## カバレッジ所見                      ← 構造的欠陥（自己採点/ゼロケース/死フィクスチャ）
## 推奨着手順                          ← gate-neutral な quick win を先頭に、担当レイヤー付き
## Swept-and-CORRECT                   ← 正しさを確認済みの表面（安心材料・次回の重複防止）
```

Deliver the Japanese summary (総評 + severity table + recommended order) directly to the user; do
not fix anything unless asked.
