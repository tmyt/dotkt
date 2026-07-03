# kotlin/clr specialist agents

Five specialist subagents, one per pipeline stage, so a large change can be split along the **layer
boundaries** the architecture already defines (`docs/ship-tasks.md` §0). Each agent's system prompt
encodes *its layer's contract and the boundary it must not cross* — so boundary violations (CLR
knowledge leaking into kotc, Kotlin knowledge into ilemit) are prevented by construction, not by
hope.

## The pipeline and who owns what

```
.NET dll ──facadegen──► kotlin metadata ─┐
                                          ├─ kotc ──► BIR ── bir2cir ──► CIR ── ilemit ──► CIL
   user .kt + stdlib.jar ────────────────┘            (reads        (reads        (reads
                                                       stdlib.jar)   ref.dll)      rt.dll)
```

| Agent | Layer | Reads | Owns | Must NOT contain |
|-------|-------|-------|------|------------------|
| **facadegen** | .NET → kotlin metadata | CLR dll | symbol surface + round-trip semantics + `System.Int32→kotlin.Int` | `@ClrIntrinsic` binding |
| **kotc** | FIR → BIR | stdlib.jar + facadegen meta | symbol resolution → BIR | any CLR knowledge |
| **bir2cir** | BIR → CIR | stdlib.ref.dll | inline/type-substitute/suspend lowering; **consumes** `@ClrIntrinsic` | passing `@ClrIntrinsic` to CIR/ilemit |
| **ilemit** | CIR → CIL | stdlib.rt.dll | CIL codegen, ilverify-clean | any Kotlin knowledge |
| **stdlib** | the `kotlin.*` library | — | `@Clr`/`@ClrIntrinsic` bindings in `runtime/stdlib/` | compiler special-casing |

## The invariant every agent shares

`@ClrIntrinsic` is **sourced from ref.dll** (stdlib agent writes it; facadegen does NOT bind it),
**consumed by bir2cir** (substituted to a plain BCL call), and **never passed to ilemit**. A fix that
violates this is a bug — see `docs/ship-tasks.md` §0.

## How to orchestrate (main agent / you)

1. **Route by layer.** A bug usually surfaces as "wrong output", but its fix lives in exactly one
   layer. Decide which (it's a lookup, per `CLAUDE.md` → *Layer placement is a lookup, never a
   question*), then delegate to that agent.
2. **Agents stop at their boundary.** If an agent finds the root cause is in a sibling layer, it
   reports that (layer + precise symptom) instead of crossing — you then route to the sibling.
   Example: an `isNaN` "wrong call" reported by kotc → routes to **bir2cir** (`@ClrIntrinsic` from
   ref.dll), not patched in kotc.
3. **Cross-layer features pipeline naturally.** "Retire an intrinsic" = stdlib binds it → bir2cir
   consumes it → ilemit stays thin. Run them in dependency order; each verifies its own slice.
4. **The gate is shared:** `./scripts/verify-il.sh` must stay green regardless of which agent acted.

## Invoking

These are project subagents (committed). Invoke via the Agent tool with `subagent_type: "kotc"`
(or `bir2cir` / `ilemit` / `facadegen` / `stdlib`). Independent slices can run in parallel; ordered
dependencies (stdlib → bir2cir → ilemit) should not.
