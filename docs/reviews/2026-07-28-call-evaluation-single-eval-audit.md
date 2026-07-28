# Call-evaluation audit: single evaluation, Kotlin order, and legal temp storage

**All `file:line` references in this document are relative to commit `6074320`** (the tip of
`agent/crossmodule-copy-single-eval` at the time of the audit). Line numbers move as soon as the redesign
starts; resolve them against that SHA, not against `main`.

**Status.** Evidence base for a redesign of call-argument evaluation in kotc/bir2cir. Not a plan of record
and not a patch — it deliberately proposes no change to any existing gate. It is the read-only enumeration
CLAUDE.md prescribes when the same fault keeps reappearing somewhere new instead of closing.

**Provenance.** Produced by a read-only pass over the tree at `6074320`. The measured program behaviours in
§2 come from three sources, marked per row: probes I ran myself, findings a cold review reproduced, and the
implementing agent's own before/after runs. Nothing here is inferred from reading alone unless it says so.

---

## 1. Conclusion

The three invariants do not inherently conflict:

1. **Single evaluation** — every value a call site supplies is evaluated exactly once, however many emitted
   positions read it.
2. **Kotlin order** — values are evaluated in the order Kotlin specifies.
3. **Legal storage** — a value is only placed in storage whose type that storage admits, including as a
   coroutine state-machine instance field.

They conflict *here* because **a call is represented twice**:

- as expressions that are later substituted into defaults, and
- as independently hoisted `var` declarations sorted ahead of the call.

When storage turns out to be illegal, the compiler must abandon one of those two representations, and
**every possible choice loses one of the three invariants**. That is a property of the representation, not of
any particular gate, which is why three successive attempts each fixed one invariant by breaking another.

The replacement is one explicit ordered call-evaluation plan whose bindings are **mandatory semantic values**
rather than an optimisation. bir2cir then chooses legal physical storage using liveness across suspension. No
layer may "decline to bind" and fall back to duplicating an expression.

## 2. Empirical evidence: three attempts, three different invariants lost

The three failed diffs are part of the specification. Each demonstrates that one corner of the design space
is empty.

| Commit | Kotlin shape | Kotlin says | Emitted | Invariant lost | Measured by |
|---|---|---|---|---|---|
| before the branch | `nextPair().copy(second = 9)` | receiver evaluated 1× | **2×**; `Triple` **3×** | 1 | me |
| `d0c8343` | byref-like (`ReadOnlySpan<Char>`) argument at a call with defaults, in a suspend body | runs, prints `d` then `31` | **`TypeLoadException`** at load | 3 | cold review; mechanism confirmed by me in code |
| `98fc20c` | `f(t, a = next(), b = a)` | `11`, `next()` 1× | **`12`**, 2× | 2 → then 1 | implementing agent, caught pre-push |
| `6074320` (tip) | byref-like value ahead of `a = defaulted(), b = a, y = x` | log `TXD`, value `111121210` | log **`DTXX`**, value **`111010221`** | 1 and 2 | cold review |

The `d0c8343` and `6074320` shapes were **measured** as working correctly on `origin/main`; for the latter the
review records why — the old filter rejected only byrefs and `@ClrRefArgument`, so an ordinary non-suspend
byref-like value used a legal local and did not duplicate the supplied expression. The `98fc20c` shape belongs
to an intermediate version that was never pushed, and its behaviour on `origin/main` was not measured. So at
least two of the attempts traded a fixed defect for a regression against shipped behaviour.

### 2.1 A test currently asserts non-Kotlin behaviour

`tests/interop/consumer/fixtures/ByRefLikeSingleEvalTests.kt:67-76` asserts the emitted order `"dT"` while
its own comment states that Kotlin requires the reverse. A redesign must flip that assertion rather than
preserve it; left as is it will read as a contract.

## 3. Why every choice in the current representation loses something

| Choice | Preserves | Breaks |
|---|---|---|
| Bind the shared value and every preceding value | 1, 2 | 3, if any temp becomes a byref/byref-like field |
| Refuse the whole temp set | 2, 3 | 1 — substitution clones the raw expression |
| Keep the filled-default temp, refuse supplied-value temps | 1, 3 | 2 — the default temp runs ahead of supplied values |
| Refuse only an unholdable filled-default temp | 2, 3 locally | 1 — its own slot and later defaults evaluate separate copies |
| Bind only selected preceding values | 1, 3 for those values | 2 — moves them across an unbound side-effecting value or address |

Mapped onto the branch:

- **`d0c8343`** made supplied and default values share temps, but storage legality only understood
  byref-*shaped slots*. The byref-like `TypeLoadException` follows directly from unconditional `var`
  promotion. Recorded at `CHANGELOG.md:56-66`.
- **`98fc20c`** represented refusal by not installing `callEvalOnceTemps`. That stopped the reordered
  filled-default hoist, but `filledArgs` retained and substituted the raw rendered expression, so wrong
  *order* became separate *evaluations*.
- **`6074320`** always installs the filled-default list again while returning an empty supplied-value hoist
  list. A legal filled default is shared but moves ahead of an unholdable supplied value; if the filled
  default is itself unholdable its expression is substituted raw again. Both residuals are explicit at
  `toolchain/kotc/src/main/kotlin/kotc/backend/BirEmitterExpressions.kt:198-207` and `:232-247`.

## 4. Complete inventory

### 4.1 kotc — binding and substitution state

- `toolchain/kotc/src/main/kotlin/kotc/backend/BirEmitter.kt:286-302` — `evalOnceSubst` maps an IR expression
  identity to its temporary read; `callEvalOnceTemps` is a *separate* ordered list to which filled defaults
  are appended later. **These two are the double representation at the heart of the problem.**
- `BirEmitterExpressions.kt:87-109` — `expr` consults `evalOnceSubst` first, else runs the call pre-pass and
  wraps the completed call in a `valueBlock` containing all collected `var`s.
- `BirEmitterExpressions.kt:264-282`, `BirEmitterCalls.kt:227-265` — `captureSubst` is a *third* substitution
  channel: same-module default filling temporarily installs argument/default/receiver JSON **strings**, and
  `IrGetValue` returns those strings.

### 4.2 kotc — supplied-value pre-hoist

All gates live in `hoistCallValuesReadByDefaults`, `BirEmitterExpressions.kt:133-218`:

| Lines | Decision |
|---|---|
| 134-136 | only function-access expressions with a defaulted regular parameter participate |
| 137-139 | source-spliced inline calls are excluded — bir2cir's `InlineSplice` becomes their owner |
| 140-157 | same-module readable omissions and reconstructed cross-module receiver reads are discovered separately |
| 158-174 | receiver-kind and enclosing-instance reads are mapped independently |
| 175-177 | supplied values enumerated in callee-parameter positions |
| 178-185 | `filledDefaultsReadByLater` can extend the binding range through every supplied value |
| 186-207 | if any non-stable value in that range has an address-taking slot or a type `canHoldInTemp` refuses, **an empty list is returned for every supplied value** |
| 208-217 | otherwise all non-stable values through `last` become `var`s and enter `evalOnceSubst` |

"Stable" here means a constant, or an immutable non-ref-cell local/parameter read: `:250-255`.

### 4.3 kotc — filled-default hoist (a second, independent mechanism)

- `BirEmitterCalls.kt:289-315` — `filledDefaultsReadByLater` independently decides which omitted default needs
  a binding: scans only readable same-module default IR, rejects stable expressions, looks for a later
  omitted default referencing the parameter.
- `BirEmitterExpressions.kt:221-247` — `bindFilledDefaultOnce` independently appends the rendered default to
  `callEvalOnceTemps`. Its order key is `parameter-count + parameter-index`, i.e. *after* every
  supplied-value key. **If the parameter type is unholdable it returns the rendered expression unchanged.**
- That unchanged return is not harmless: `filledArgs` stores it in `filledByParam`
  (`BirEmitterCalls.kt:166-174`) and later defaults substitute it literally (`:278-284`), so one unbound
  `next()` becomes one copy in its own slot and another inside `b = a`.

### 4.4 kotc — default sources and substitutions

`filledArgs`, `BirEmitterCalls.kt:90-116`, performs four materially different substitutions:

1. `:170-175` — a supplied argument goes through `argExpr`.
2. `:179-198` — a referenced synthetic data-class `copy` default is reconstructed as a receiver-field read;
   the receiver JSON may be the pre-hoisted local. `reconstructedDefaultReceiver` (`:332-355`) is shared with
   the pre-pass so reconstruction and dependency discovery cannot disagree.
3. `:199-225` — other cross-module omissions become positional `defaultArg` placeholders for bir2cir.
4. `:227-265` — same-module defaults install earlier parameter values, receiver values and enclosing-instance
   values in `captureSubst`, then render the default.

Receiver rendering is lazy and split by dispatch / extension / enclosing kind because rendering itself has
synthesis side effects: `:132-164`. The ordinary call path runs `filledArgs` once and shares the result
across extension and non-extension emission: `:1714-1746`.

### 4.5 kotc — address-taking slots are a distinct concern from value storage

`argExpr`, `BirEmitterCalls.kt:1873-1907`: a `ClrRef<T>` unwraps `byref(x)` and emits the addressable lvalue;
`@ClrRefArgument` also emits an addressable argument. Neither is an ordinary copied value. The
address-*producing* expression can still have side effects (`byref(mk().field)`), so hoisting values around an
unbound address changes order even though no value temporary is appropriate.

### 4.6 kotc — first storage-legality oracle

`canHoldInTemp`, `toolchain/kotc/src/main/kotlin/kotc/backend/BirEmitterTypes.kt:494-520`, rejects a BIR
`ByRef` (looking through nullable/oblivious wrappers) and a declaration known to be CLR byref-like, while
deliberately permitting caller-frame type variables. The byref-like fact arrives through frontend injection
metadata, with `kotlin.clr.Span` added explicitly:
`toolchain/kotc/src/main/kotlin/kotc/frontend/ClrTypeInjection.kt:182-194` and `:454-458`.

**This is a layer-contract violation, not merely a smell.** kotc is deciding legality for the strongest
possible CLR storage because it cannot express the temporary's intended lifetime. Under this project's
principles kotc carries Kotlin facts and bir2cir fixes the physical CLR representation; a storage-legality
predicate in kotc is bir2cir's decision made in the wrong layer. Any change that keeps `canHoldInTemp` —
including a "narrow" fix that avoids widening — makes that violation permanent in `main`.

### 4.7 kotc — non-expression call sites

Constructor delegation and enum calls cannot be wrapped normally, so the entire temp list is embedded in a
`valueBlock` around the *first argument*: `BirEmitterCalls.kt:357-365`. Consumers:
`BirEmitterDeclarations.kt:291-300` (plain enum entry init), `:328-337` (per-entry enum subclass base args),
`:972-995` (`this`/`base` delegation). Constructor expressions otherwise rely on the outer `expr` wrapper
while `filledArgs` lazily builds the regular arguments: `BirEmitterExpressions.kt:343-404`.

### 4.8 kotc/bir2cir — inline calls are a third mechanism

kotc excludes source-spliced inline calls from its pre-pass and emits index-aligned call values with `null`
omissions instead: `toolchain/kotc/src/main/kotlin/kotc/backend/BirEmitterInline.kt:174-188`, `:240-277`.

`toolchain/bir2cir/InlineSplice.cs:336-432` then creates a temp for the dispatch receiver, processes
parameters in order, materialises omitted defaults, substitutes carrier tokens with already-bound argument
locals, and **unconditionally creates a `var` for every non-lambda parameter/default**. There is no
byref/byref-like/open-type legality test here at all — a third, independent conception of what an argument
temporary may hold.

### 4.9 bir2cir — cross-module default splice

`DefaultArgSplice` runs after inline splice and before type lowering: `toolchain/bir2cir/Program.cs:280-291`.
Its binding machinery is independent of kotc's:

- `DefaultArgSplice.cs:285-308` — scans carriers for `{this}` / `{defaultArgParam}` tokens, collecting positions.
- `:164-177` — `lastRead` defines the prefix that must be hoisted to preserve order.
- `:178-187` — a single `bindable` flag rejects the **entire prefix** if any argument, placeholder or receiver
  has no acceptable `TempType`.
- `:188-208` — placeholders filled in ascending parameter order; a just-filled value is hoisted only while
  `bindable` holds.
- `:218-225` — the receiver is separately inserted at the front of the temp list.
- `:141-152`, `:364-375` — each hoist creates a BIR `var`; expression calls are wrapped in a `valueBlock`.
- `:232-282` — delegations carry the same first-argument placement hack as kotc.
- `:472-500` — substitution is literal tree cloning: `{defaultArgParam N}` becomes a deep clone of `args[N]`.
  **Therefore when `bindable` is false, later defaults receive clones of raw filled expressions and
  invariant 1 is lost.**

### 4.10 bir2cir — second storage-legality oracle, disagreeing with the first

`TempType`, `DefaultArgSplice.cs:315-362`: tries the declared slot type; rejects `ByRef`, byref-like types and
**open callee-frame type variables**; if the slot type is open, tries the value's `sty`/`type`; rejects values
with no usable type. Byref-like identity is read independently from reference metadata
(`toolchain/bir2cir/ReferenceMetadataIndex.cs:573-587`), and `kotlin.clr.Span` is canonicalised before normal
type lowering (`toolchain/bir2cir/BirTypeLowering.cs:24-28`).

**kotc and bir2cir disagree about open types:** a caller-frame `T` is accepted by `canHoldInTemp` while an
unresolved callee-position `Tv` is rejected by `TempType`.

### 4.11 bir2cir — the decisive physical step has no legality gate

`toolchain/bir2cir/SuspendColdLowering.cs:1093-1101` calls `CollectVarFields` over the body once a state
machine is required; `:1179-1205` makes **every non-handler `var` a state-machine field, without liveness
analysis and without a type-legality test**. This is where an illegal temp becomes an unloadable type.

## 5. One mechanism that maintains all three

Introduce one explicit BIR call-evaluation plan.

```text
bindings, in Kotlin evaluation order:
  supplied receiver
  supplied arguments
  omitted default values, in declaration order

each binding:
  id
  expression
  caller-instantiated semantic type
  kind = value | address
  consumers

invoke:
  receiver  = binding-ref
  arguments = binding-ref...
```

Rules:

1. **kotc constructs the ordered semantic plan.** A supplied expression appears exactly once. Same-module
   defaults and reconstructed `copy` fields refer to binding **IDs**, never to rendered expression strings.
2. **Cross-module placeholders reserve a default-phase binding** in that plan. `DefaultArgSplice` materialises
   only the missing default expression and translates carrier tokens into binding references. It does not
   clone argument expressions, discover a prefix, or create temps.
3. **Inline splice consumes the same bindings** rather than creating its own unconditional parameter `var`s.
4. **bir2cir performs one liveness-aware physical lowering after all splices:**
   - a binding not live across a suspension may be an ordinary scoped CLR local — including a byref-like local
     or a managed-pointer/address binding;
   - a binding live across a suspension becomes a state-machine field **only when the fully resolved CIR type
     is legal for an instance field**;
   - caller type variables are resolved in the caller's generic frame first;
   - an address/byref/byref-like value that genuinely must cross a suspension is **unrepresentable**:
     compilation fails at that binding with a precise diagnostic, and never silently duplicates or reorders
     the expression;
   - unknown or unresolved storage types are likewise errors, not permission to abandon the binding.

On the current repro this needs no compromise: the byref-like call value dies before the later suspension, so
it stays a legal local instead of being blindly promoted. If a later argument suspends while the byref-like
value is still required, rejection is correct — no CLR representation satisfies all three invariants there,
and a diagnostic is the only honest outcome.

## 6. What the mechanism deletes

Deleting is the point; a mechanism that only adds leaves the conflicting ones in place.

**From kotc:** `evalOnceSubst`; `callEvalOnceTemps`; `withCallValuesBoundOnce`;
`hoistCallValuesReadByDefaults`; `filledDefaultsReadByLater`; `bindFilledDefaultOnce`; `declaringFirstArg`;
`canHoldInTemp` and `isByRefNode`; the `filledByParam` map of rendered expression strings; the temporary
installation of call values into `captureSubst`; the two-tier integer order keys; the constructor/enum
"declare temps in the first argument" placement.

`reconstructedDefaultReceiver` survives **only** as the Kotlin semantic fact that supplies a `copy` default's
dependency; it stops being a hoist gate.

**From bir2cir:** `DefaultArgSplice.CollectReadPositions`; `lastRead` and the all-or-nothing `bindable`
prefix; `TempType` with its local `IsByRefLike`/`IsOpen` and `IsStableValue`; `Hoist`, `HoistFirst`,
`HoistAt`; deep-cloning call expressions in `SubstituteTokens`; default-splice `valueBlock` wrapping and
delegation first-argument wrapping; `InlineSplice`'s independent argument/default temp construction; suspend
lowering's unconditional "every non-handler `var` is a field" rule, replaced by liveness-selected storage; the
call/new arm of `RewriteEvalOrder`, since the explicit plan already carries the order.

Metadata lookup, carrier materialisation, `copy` reconstruction and the byref-like metadata oracle all
remain. They supply **facts**; they stop deciding whether Kotlin semantics may be weakened.

## 7. Scope note on a "narrow" fix

Fixing only the `copy` reconstruction — making it consume an existing binding instead of re-rendering the
receiver — was considered and rejected as a merge candidate. It would leave in place: the double
representation (§4.1), three disagreeing storage oracles (§4.6, §4.10, §4.8), the unconditional `var`-to-field
promotion (§4.11), and `canHoldInTemp` in kotc (§4.6). The last of those is the decisive objection: a narrow
fix is not a small correct change but the permanent installation of a small layer violation, and the next
change in this area meets the same wall.
