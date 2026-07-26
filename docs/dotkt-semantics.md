# DotKt semantics — how Kotlin maps to the CLR, and where it deliberately differs from Kotlin/JVM

DotKt compiles Kotlin to a normal .NET assembly. A lot of Kotlin's surface is **JVM-shaped accidental complexity**
(erased generics, `@Metadata`, the Continuation ABI, JVM string conventions). On the CLR, DotKt **reinterprets or
discards** those rather than reproducing them. This page is the canonical list of those behavioral differences and
non-obvious interpretations — the things a Kotlin/JVM developer would otherwise be surprised by. Feature-by-feature
deep dives are linked per section.

Guiding principle: *Kotlin carries JVM accidental complexity; on the CLR, identify it and discard it — don't
reproduce it.* (Memory `clr-not-jvm-discard-jvmisms`.)

**The acceptance test for every behavior choice is: *consistent, documented, convincingly
explainable* — never "matches JVM".** Kotlin/JVM behavior is cited throughout this page only as a
*reader reference* (what a Kotlin/JVM developer would expect), never as a compatibility target.
Resolution order: ① where the Kotlin spec/KDoc contract fixes a behavior, DotKt honors it by default
(*Kotlin contract* — e.g. §5a float total order); ② where Kotlin leaves it unspecified, DotKt takes
the CLR-native form (*deliberate CLR choice*, with the reason stated); ③ where CLR/interop
consistency convincingly outweighs the KDoc letter, DotKt may deviate even from the contract
(*interop-first deviation* — exemplar: §5b-ter case mapping, `"ß".uppercase()` stays `"ß"`). A
deviation is acceptable iff it passes all three conditions of the test; hand-forcing a JVM value with
"matches JVM" as the justification passes none of them. Every deviation is recorded here.

> Looking for the friendly tour instead of the canonical reference? Read
> [`docs/user/kotlin-on-clr-differences.md`](user/kotlin-on-clr-differences.md) first.

**Contents**

| § | Deviation |
|---|---|
| [1](#1-kotlin-packages--net-namespaces) | Kotlin packages → .NET namespaces |
| [2](#2-generics-are-reified--so-kotlin-reified-is-almost-a-no-op) | Reified generics — `reified` is (almost) a no-op |
| [3](#3-inline-happens-at-emit-time-and-is-decoration-unless-a-lambda-literal-is-passed) | `inline` is emit-time, decoration unless a lambda is passed |
| [4](#4-suspend-fun--an-async-taskt-function-hot-not-cold) | `suspend fun` = an async `Task<T>` function; **hot, not cold** |
| [5](#5-primitive-stringification-is-clr-native-not-kotlinjvm-cosmetics) | CLR-native stringification; `String.format` = .NET composite format |
| [5b](#5b-charsequence-is-string-on-the-clr--an-immutable-snapshot-not-a-live-view) | **`CharSequence` is `string`** — snapshot, not live view |
| [5c](#5c-mapmutablemap-both-erase-to-idictionarykv--read-only-ness-is-frontend-enforced) | `Map`/`MutableMap` → `IDictionary<K,V>` |
| [5d](#5d-appendable-is-systemtextstringbuilder) | `Appendable` is `System.Text.StringBuilder` |
| [5e](#5e-enum-classes-have-two-clr-shapes) | Enum classes: basic → real CLR `enum`, rich → singleton class |
| [5f](#5f-value-class-is-a-real-wrapper-class-never-erased) | `value class` = a real class (never erased) |
| [6](#6-consuming-a-dotkt-assembly-as-kotlin--what-rides-metadata-vs-needs-an-attribute) | Round-trip: what rides metadata vs. needs an attribute |
| [7](#7-default-arguments--a-two-tier-rule-native-metadata-else-a-carried-bir-expression) | Default arguments — the two-tier rule |
| [8](#8-reverse--cross-assembly-interop) | Reverse / cross-assembly interop |
| [8b](#8b-dual-representation-import-systemtextstringbuilder-vs-kotlintextstringbuilder--two-typed-views-of-one-clr-type) | Dual view: imported .NET type vs. its stdlib alias |
| [8c](#8c-injected-net-static-members-implicit-typemember-works-companion-optional) | Injected .NET statics: implicit `Type.member` works |
| [8d](#8d-net-event-subscriptions-and-closeable-tokens) | .NET events use closeable subscriptions |
| [9](#9-reference-type-nullability--net-nrt-un-annotated-net-types-are-platform-types) | Nullability ⇔ .NET NRT; platform types `T!` |
| [10](#10-round-trip-fidelity-audit--what-re-consuming-a-dotkt-assembly-as-kotlin-loses) | Round-trip fidelity audit (incl. pinned-2.4.0 limitations) |

---

## 1. Kotlin packages → .NET namespaces

- **JVM:** package = a path; a class's binary name is `pkg/Name`. The package always survives.
- **DotKt:** a Kotlin package is projected to the **.NET namespace** — `package geom; class Vec` emits `.NET geom.Vec`,
  and the file-facade class is `geom.<File>Kt`. Nested types stay **simple-named** (their outer type carries the
  namespace, i.e. `geom.Outer+Inner`). Root-package code is unchanged (no namespace).
- **Why it matters:** without this, two classes with the same simple name in different packages (`alpha.Box` +
  `beta.Box`) would both emit `.NET Box` and **collide** — a hard error. It's also the prerequisite for consuming a
  packaged DotKt library across an assembly boundary (`import geom.Vec`).
- Gotcha: this is recent (2026-06-24). The injector derives the Kotlin package from the .NET namespace, so a
  consumer's `import geom.Vec` resolves only because the emit side now qualifies the name.

## 2. Generics are reified — so Kotlin `reified` is (almost) a no-op

- **JVM:** generics are **erased**. `reified` exists only to recover the type argument at the call site, and it
  *requires* `inline` (the type is baked in during inlining). You can't call a reified function non-inlined, and you
  can't pass a non-reified type parameter to a reified one.
- **DotKt:** the CLR has **real reified generics**. `inline fun <reified T> foo()` is emitted as an ordinary generic
  method `foo<T>()`; the body's `T::class` / `is T` / `as? T` become `typeof(T)` / type checks on the real runtime
  type. So:
  - `reified` is **decoration** — DotKt drops it; the function is just a generic method.
  - Dropping it *removes* the JVM constraint: a consumer can pass a **non-reified** type parameter
    (`fun <U> bar() = foo<U>()` is fine on the CLR, an error on the JVM).
  - There is **no `@Metadata`/reified attribute** to round-trip.
- **Corollary — a star-projected collection (`Map<*,*>` / `List<*>` / `Iterable<*>` / `Collection<*>`) binds to the
  NON-generic BCL interface, because reified generics are INVARIANT.** On the JVM `x is Map<*,*>` and a subsequent
  `x as Map<*,*>` erase to a raw `Map`, so a `Dictionary<int,int>` passes trivially. On the CLR the star projection
  erases to `Map<Any?,Any?>` = the generic `IDictionary<object,object>`, which a `Dictionary<int,int>` does **not**
  implement (no value-type covariance) — a naive `castclass`/`isinst` to it fails. So DotKt lowers a star-projected
  `is`/`as` (and `.size`/`[i]`) to the **non-generic** `System.Collections.IDictionary`/`IList`/`ICollection`/
  `IEnumerable`, which every value-type-arg BCL collection implements; `println` of such an erased value renders via
  the runtime-detecting `clrElemToString`. (A `<*>` value can only be used non-generically anyway.) This is the same
  invariance that forces §5c (`Map`/`MutableMap` both → `IDictionary<K,V>`). #60.
- **Corollary — an UNCHECKED generic cast (`as T`, incl. `@Suppress("UNCHECKED_CAST")`) is checked
  EAGERLY at the cast site, not erased.** kotc emits every `x as T` (including a smart-cast) as a
  plain `cast` BIR node regardless of whether `T` is reified (`BirEmitterExpressions.kt:226-229`);
  ilemit lowers it to `unbox.any`/`castclass !!T` at that exact IL offset
  (`Emitter.Expressions.cs:539-558`) — because CLR generics are reified, that instruction is a REAL,
  immediate check. On the JVM the same cast is ERASED: it is either a no-op (`(List<String>)
  listOf(1, 2, 3)` succeeds silently; a `ClassCastException` fires only when an element is later USED
  as `String`) or, inside a generic function, performs **no check in the callee at all**
  (`fun <T> f(x: Any?) = x as T` never faults on the cast itself). On DotKt both throw
  `InvalidCastException` **immediately** — `listOf(1, 2, 3) as List<String>` throws on the cast line,
  and `f<String>(42)` throws INSIDE `f`, not at `f`'s call site. Code that relied on JVM
  erasure/heap-pollution tolerance (a common `@Suppress("UNCHECKED_CAST")` pattern) fails earlier and
  in a different place on the CLR. **Edge case:** a non-null `String` variable holding `null` via an
  unchecked cast still renders as `""` in string concatenation (`String.Concat` treats a null
  reference as empty), whereas a genuinely-nullable `String?` holding `null` renders `"null"` — same
  value, different textual result, depending on how the null got there.
- Deep dive: §3 (inline).

## 2b. `tailrec` IS tail-call optimized — deep tail recursion runs in constant stack (matches Kotlin/JVM)

- **JVM:** `tailrec fun` is rewritten by the Kotlin frontend into a **loop**, so a self-tail-recursive function
  runs in **constant stack** — `tailrec fun sumTo(n, acc) = if (n==0) acc else sumTo(n-1, acc+n)` handles
  `sumTo(1_000_000, 0)` fine.
- **DotKt (2026-07-06):** kotc emits the **same tail-call optimization**. A self-tail-call in a `tailrec` fn is
  rewritten to a **back-jump to the method entry**: evaluate the call's args into temporaries (so a later arg
  reading an earlier param — `sumTo(n-1, acc+n)` — is not corrupted), reassign the parameters, then `goto` the
  loop head. The frontend's own tail-position analysis (`collectTailRecursionCalls`) drives it — our pipeline
  runs Fir2Ir straight into the backend, skipping the JVM lowerings, so kotc reapplies this one. Deep tail
  recursion that used to overflow the CLR stack now runs in constant stack: `sumTo(1_000_000, 0)` returns
  `500000500000`. Covered for the self / multi-branch-`when` / extension-receiver (`__self` reassigned) / member
  (dispatch `this` unchanged) forms.
- Coverage: `tests/basic/fixtures/NumericAndTupleTests.kt`. (The `tailrec` **modifier** itself is still compile-time
  only and does not round-trip as a declaration fact — §566 — but the behavior now matches.)

## 2c. `super.X()` from an override is a NON-virtual `call` to the base slot (matches Kotlin/JVM, #14)

- **Kotlin/JVM:** a `super.method()`/`super.prop` call is statically bound (`invokespecial`) to the **super-class
  slot** — it never re-dispatches to the calling class's override. `override fun greet() = "d+" + super.greet()`
  reaches the base body exactly once.
- **DotKt (2026-07-14):** kotc now reads `IrCall.superQualifierSymbol` and emits a super-qualified instance call as a
  **non-virtual `call`** (the shared `isVirtualInstanceCall` helper forces `virtual:false` for every super call —
  method, member-extension, property get/set, index, and .NET-interop sites). This is the `base.M()` shape C# emits.
  Before the fix kotc emitted `super.greet()` identically to `this.greet()` (`callvirt`), so the call re-dispatched by
  the receiver's runtime type back to the override → **infinite recursion / stack overflow**. A normal virtual call
  through a base-typed variable (`(b: Base).greet()`) is unchanged — still `callvirt` to the override. Covers
  `super.<prop>`, N-level `super` chains, `super` to a user base's `toString()`, and `super<IFace>.foo()` to a Kotlin
  interface default (DIM). Coverage: `tests/basic/fixtures/RuntimeTypesVisibilityAndCrossFileTests.kt` and
  `tests/interop/consumer/fixtures/ClrObjectModelTests.kt`.

## 3. `inline` happens at EMIT time, and is decoration unless a lambda literal is passed

This is the single most surprising deviation, so it gets the most detail.

- **JVM:** inline functions are inlined during a frontend/IR lowering; the body is also serialized into `@Metadata`
  so other modules can re-inline at *their* call sites.
- **DotKt pipeline (four layers: `facadegen` / `kotc` / `bir2cir` / `ilemit`).** The
  frontend is `…Fir2Ir then ClrBackendPhase` — **there is NO JVM `FunctionInlining` lowering.** The IR that reaches the
  backend still has un-inlined `inline` calls. **Inlining (and the `[KotlinInline]` splice) is a `bir2cir` (BIR→CIR)
  responsibility.** kotc projects the call and caller-lambda body without introducing CLR vocabulary:
  ```
  Kotlin IR call → kotc callInline BIR → bir2cir raw-BIR splice → CIR → ilemit
  ```
- Consequences:
  - **`inline` and `reified` are pure decoration UNLESS the call passes a lambda LITERAL.** A lambda-less `inline fun`
    (`inline fun twice(x: Int) = x + x`) is emitted as an ordinary method and called normally — the JIT inlines it.
    The modifier does nothing in DotKt's own codegen.
  - Same-module inline with a lambda (incl. **non-local return** and **crossinline**) works — the IR body is present
    and spliced (`il-inline`, `il-inline2`, `il-xinline`).
  - **Cross-module:** an injected stub has `body == null`, so it's never the IR-splice case. Lambda-less / no-non-local-
    return inline degrades to a plain (or generic) call — correct. The ONE case that can't degrade is a **non-local
    `return` through a lambda** (it must return from the *caller's* frame, which only inlining achieves).
- Cross-module non-local-return IS supported (2026-06-24), and — because inlining is over (near-)BIR — it's
  **much lighter than JVM's `@Metadata`** (no IR deserializer): `[KotlinInline]` carries the function's closed raw-BIR
  payload, the consumer's `bir2cir` reads it from the `--ref`'d assembly, re-hoists any carried generated delegate
  targets, and splices it before codegen (a `return` in the spliced lambda body becomes the caller's `ret`). Full
  mechanism + scope in
  `docs/design-kotlin-metadata-attributes.md`.
- Pitfall (verified, do NOT do this): marking an injected body-less function `inline` *without* carrying the body lets
  the frontend accept a non-local return but leaves nothing to splice → `InvalidProgramException` at runtime (worse
  than the clean compile error). `inline` restoration and the carried body are a package deal.
- **`repeat(n) { … }` honors a non-local `return`** (#75). For a literal-lambda `repeat`, kotc splices the lambda body
  UN-CLOSURED into a `callInline` BIR node (carried in the caller's scope); `bir2cir`'s `InlineSplice` pass wraps that
  body in the counted loop (`repeatInline`) with the body SPLICED, not delegate-invoked. `n` is evaluated once, the index
  runs `0..n-1`, a non-local `return` inside `repeat { … return … }` returns from the enclosing function, and a
  `return@repeat` acts as `continue`. (A non-literal action — e.g. `repeat(n, ::fn)` — still falls through to the
  delegate loop `RepeatInlineLowering`, which does not honor a non-local return; but Kotlin only permits a non-literal
  argument for a `noinline` param, and `repeat`'s action is not `noinline`, so this shape is a callable reference only.)
  - **Edge (loud, not silent):** a `return@repeat` inside a `try`/`finally` in the repeat body branches out of the
    protected region (`goto`, not `leave`) → `ilverify`/JIT rejects it. This is the general inline-splice hazard (it also
    affects a mid-body `return` in a user same-module inline fn), not specific to `repeat`; use a `for` loop if you need a
    labeled early-exit out of a `try` inside the loop.

## 4. `suspend fun` = an async `Task<T>` function; hot, not cold

**A `suspend` function IS, on the CLR, an async function returning `Task<T>`, and a suspend CALL is an `await`.**
This is a user-stated foundational deviation, not an implementation detail.

- **JVM:** a `suspend fun f(): T` compiles to `Object f(…, Continuation)` — the Continuation is an explicit parameter,
  CPS the public ABI.
- **DotKt:** the **public CLR ABI is `Task<T> f(…)`** — the Continuation never appears in the signature (it's the
  internal lowered form, with a `Task` sink). Calling a suspend function from suspend context is emitted as an
  **await** of that `Task<T>`. A C# caller `await`s it natively; a Kotlin caller in another module sees a
  `suspend fun` again (restored from a `[KotlinFunction(Suspend)]` attribute, with the `Task<T>` unwrapped to `T`).
- **Execution is HOT, not cold.** A .NET `Task` starts running when created — so invoking a suspend function starts
  its execution immediately, exactly like C# `async`. This deliberately deviates from the kotlinx-coroutines
  cold-by-default framing (a `suspend` body that only runs when awaited/launched). Kotlin's *language* semantics for
  a direct suspend call (call = run to the first suspension and continue) are preserved; what does NOT carry over is
  the kotlinx-style cold-start expectation around builders. Structured concurrency (`Job`/`CoroutineScope`) is a
  separate later track.
- Gotcha: a member `suspend fun` returning a **user type** drove out a Reflection.Emit limitation
  (`AsyncTaskMethodBuilder<UserT>` is a TypeBuilder instantiation) — fixed by re-anchoring those members via
  `TypeBuilder.GetMethod`.
- **Invoking a stored suspend function VALUE of arity N uses a cold `create(args)` slot the JVM lacks.** A
  value of type `suspend (A,B,…) -> R` is a cold `SuspendLambda` state machine (a `BaseContinuationImpl`),
  not a `FunctionN`. Invoking it (`f(a,b,…)`) drives the SM to its first suspension via the stdlib helpers.
  Arities 0/1 use the fixed `create(completion)` / `create(value, completion)` slots (JVM parity); arity ≥ 2
  uses a DotKt-specific general slot `BaseContinuationImpl.create(args: Array<Any?>, completion)` — the N
  invoke args are boxed into an `Array<Any?>` and unpacked into the SM's param fields. The JVM has no such
  slot (there an arity-2+ suspend lambda is driven through the generated `FunctionN.invoke(a,b,…,completion)`
  bridge, which DotKt does not synthesize); the array-create is the CLR cold-core generalization. Purely an
  internal ABI difference — the language semantics of `f(a,b,…)` are unchanged.
- Deep dives: `docs/coroutine-abi.md` (the ABI contract) and
  `docs/design-coroutine-cold-core-task-bridge.md` (the internal implementation).

## 4a. `Task.await()` resume precedence — interceptor > captured SynchronizationContext > inline (#3/#7)

When a `Task<T>.await()` (the `kotlin.clr.await` marker) genuinely SUSPENDS, the resume thread/context is chosen
by this precedence, mirroring Kotlin/JVM's "the interceptor owns dispatch, SyncContext capture is only the default
policy":

1. **`ContinuationInterceptor` present** in the coroutine context → the resume routes through
   `ContinuationInterceptor.interceptContinuation` (the interceptor-wrapped continuation's `resumeWith` decides the
   resume). This is Kotlin's dispatcher mechanism (e.g. a UI dispatcher) and it takes PRECEDENCE. The cold-core
   `ContinuationImpl.intercepted()` performs the lookup+wrap (cached, JVM parity); bir2cir routes the await-point
   `OnCompleted` callback through `this.intercepted().resumeWith(...)`.
2. **No interceptor, `await(captureContext = true)` (the default)** → the CLR `TaskAwaiter` captures
   `SynchronizationContext.Current` and `Post`s the resume onto it (mirrors .NET
   `ConfigureAwaitOptions.ContinueOnCapturedContext`).
3. **No interceptor, `await(captureContext = false)`** → `ConfigureAwait(false)` awaiter, resume runs inline on the
   completing thread (no SyncContext capture).
- The coroutine context propagates DOWN the cold-entry call chain: a nested `suspend fun`'s state machine inherits its
  completion's context, so an interceptor installed at the coroutine root is honored at a nested-fun await too.
- Caveat (double-hop, an accepted follow-up): with an interceptor AND the default capturing awaiter, the awaiter's
  `OnCompleted` still runs on the captured SynchronizationContext, which then re-dispatches to the interceptor — so the
  interceptor owns the FINAL resume (precedence holds) but the SyncContext remains on the delivery path (a blocked
  SyncContext pump would starve delivery). Suppressing the SyncContext capture when an interceptor is present (a
  runtime branch, ideally a stdlib `taskinterop` await-registration helper) is the cleaner consolidation, deferred.

## 4a-bis. Cancellation-exception fidelity across the Task bridge is ONE-WAY (Kotlin→.NET fixed; .NET→Kotlin is a 0.9.8/Track-2 gap)

Cancellation crosses the suspend↔`Task` boundary as an EXCEPTION, and the two exception vocabularies differ: Kotlin
signals cancellation with `kotlin.coroutines.cancellation.CancellationException` (which extends `IllegalStateException`
on the CLR — `CancellationExceptionClr.kt`), while .NET signals it with `System.OperationCanceledException` (and its
`TaskCanceledException` subtype) plus the type-signaled `Task.IsCanceled` protocol. These are DISJOINT type hierarchies
(a Kotlin CE is NOT an OCE), so the bridge maps them explicitly, and only the **Kotlin→.NET** direction is mapped:

- **Kotlin→.NET (FIXED, #105).** When a coroutine completes by throwing — either kind of cancellation — `RootContinuation`
  (the Task-bridge sink) completes the bridge `Task` as **CANCELED** (`TrySetCanceled` → `IsCanceled == true`), not
  FAULTED. Both a .NET `OperationCanceledException` (its originating `CancellationToken` carried through, #116) AND a
  Kotlin `CancellationException` (no token — .NET's canceled-Task protocol is type-signaled, so the CE object is NOT
  preserved into the .NET exception; carrying it via `TrySetException` would leave `IsCanceled == false` and break every
  .NET structured-concurrency consumer) route to a CANCELED Task. A .NET consumer of a Kotlin `suspend fun` therefore
  observes idiomatic cancellation (`TaskCanceledException` on `await`, `Task.IsCanceled`).
- **.NET→Kotlin (DELIBERATE GAP, 0.9.8/Track-2).** The REVERSE is NOT bridged: a plain `.await()` of a canceled .NET
  `Task` resumes the Kotlin coroutine with a `TaskCanceledException` (an `OperationCanceledException`), which is NOT a
  `kotlin.coroutines.cancellation.CancellationException` — so a Kotlin `catch (e: CancellationException)` does NOT catch
  it, and Kotlin structured-concurrency (`Job` cancellation, `NonCancellable`, cancellation-aware `finally`) does not
  recognize it as cancellation. `.await()` deliberately does NOT auto-insert an inbound OCE→CE mapping (matching the
  #86 "nothing auto-inserted" principle); the planned fix is an OPT-IN interop adapter `Task.awaitCancellable()`
  (OCE→CancellationException) shipped with the structured-concurrency track (it is meaningless without `Job`/
  `CoroutineScope`). A future opt-in adapter can bridge this boundary; remaining work is tracked in GitHub Issues.

## 4c. `.await()` binds to the .NET AWAITABLE PATTERN (GetAwaiter), not to Task (#10)

`await` is NOT Task-specific — it binds to the same **awaitable pattern** the C# compiler uses, so ANY .NET awaitable is
awaitable from Kotlin with zero per-type compiler support. A type `X` is awaitable IFF it has a `GetAwaiter()` — a public
parameterless instance MEMBER, **or** a referenced `[Extension] static GetAwaiter(this X)` — returning an *awaiter* that
has `bool IsCompleted { get; }`, `T GetResult()`, and implements `INotifyCompletion` (its `OnCompleted(Action)` is what the
cold-core resume binds). This is the await analog of the `@ClrIntrinsic`/facadegen philosophy: bind by signature/metadata,
embed no dialect.

- **What this covers:** `Task`/`Task<T>` (member `GetAwaiter` → `TaskAwaiter[<T>]`), `ValueTask`/`ValueTask<T>` (member
  → `ValueTaskAwaiter[<T>]`, no `.AsTask()`), a WinRT `IAsyncOperation<T>` (a GENERIC *extension* GetAwaiter, awaitable only
  when the projection/support assembly providing it is referenced — a LIBRARY fact, not a compiler dialect), and any
  custom awaitable. The result type is the awaiter's `GetResult()` return (`void` → `Unit`).
- **Where it lives (layer split):** **facadegen** pattern-detects each surfaced .NET awaitable and injects a
  `suspend fun X.await(): <Result>` platform extension in `kotlin.clr` (only when a conforming GetAwaiter exists).
  **bir2cir** (`SuspendColdLowering.EmitAwaitPoint`, via `ReferenceMetadataIndex.ResolveAwaitable`) discovers the awaiter
  type + members from ref metadata and lowers the marker to the awaiter dance (`GetAwaiter` → spill → `IsCompleted`
  fast-path → `OnCompleted(resume)` + return SUSPENDED → `GetResult`). **ilemit** has no await knowledge. A member
  GetAwaiter emits `clrInstance`; a generic extension GetAwaiter emits `MyExt.GetAwaiter<TResult>(x)` (`clrGenericStatic`),
  the method type arg unified from the concrete receiver (`X<Int>` binds `TResult=Int`).
- **We bind `OnCompleted` (INotifyCompletion), not `UnsafeOnCompleted` (ICriticalNotifyCompletion):** the cold core carries
  no ExecutionContext-flowing state-machine box, so `OnCompleted` (which flows EC) is correct; `UnsafeOnCompleted` would drop
  `AsyncLocal` flow across every await. UnsafeOnCompleted is a future optimization gated on SM-level EC capture.
- **`ConfigureAwait`/`captureContext` stays Task-like:** the `await(captureContext = false)` opt-out (§4a) is offered ONLY
  for an awaitable that exposes a `ConfigureAwait(bool)` member (Task, ValueTask). A generic awaitable without it uses
  GetAwaiter directly; requesting `captureContext = false` on such a type is a compile-time error.
- Coverage: `tests/coroutines/fixtures/TaskAndValueTaskAwaitTests.kt`; custom-awaitable gaps are tracked in GitHub Issues.

## 4b. The default `lazy { }` is thread-safe (a Monitor lock, matching Kotlin/JVM and `System.Lazy`)

`lazy { }` on DotKt is thread-safe by default, exactly as on Kotlin/JVM (and matching .NET's own
`System.Lazy<T>` default of `LazyThreadSafetyMode.ExecutionAndPublication`). The mode → implementation
map (stdlib `libraries/stdlib/clr/kotlin/util/LazyClr.kt`):

- **default `lazy(initializer)`**, **`SYNCHRONIZED`**, **`PUBLICATION`**, and **`lazy(lock, initializer)`**
  → `SynchronizedLazyImpl` — memoization guarded by a `System.Threading.Monitor` lock (the stdlib
  `@ClrIntrinsic` `monitorEnter`/`monitorExit` in `kotlin.concurrent.atomics`; no compiler lowering).
  `PUBLICATION` is served — if more strictly than the spec requires (it permits multiple initializer
  runs) — by the same single-init locked impl; this is correct, only conservatively so.
- **`NONE`** → `UnsafeLazyImpl` (no synchronization), as on JVM.

**Locking discipline — double-checked locking (DCL) with a lock-free fast-path read.**
`SynchronizedLazyImpl` uses the classic DCL shape, exactly like Kotlin/JVM's `SynchronizedLazyImpl`:
the `value` getter first does a **lock-free `@Volatile` read** of the backing field, and takes the
Monitor lock only on the still-uninitialized slow path, where it re-checks the field (the second
"check") before running the initializer exactly once. So a fully-initialized `lazy` costs a single
volatile field load — no lock. The DCL fast path is memory-safe **because the value field is
`@kotlin.concurrent.Volatile`**, which on the CLR is a real volatile field (`modreq(IsVolatile)` +
`volatile.` prefix — §4c), giving the fast-path read acquire semantics and the publishing write
release semantics on weak-memory architectures (ARM). (This was an always-lock stopgap until
`@Volatile` became real — the DCL fast path was memory-unsafe while `@Volatile` was a no-op.) The
initializer runs inside the locked critical section, and the lock is released in a `finally` so a
throwing initializer cannot leak the lock. (The value field is published by a single reference-typed
write of the fully-constructed value — atomic on .NET — so no torn value is ever observed; a
single-threaded gate cannot itself observe the cross-thread visibility, which rests on the `@Volatile`
modreq being the exact C# `volatile` encoding the JIT honors.)

## 4c. `@kotlin.concurrent.Volatile` = a real CLR volatile field (`modreq(IsVolatile)` + `volatile.`)

`@Volatile` on a `var`'s backing field is **not** a no-op on DotKt: it lowers to a genuine CLR volatile
field, using the **exact same encoding the C# `volatile` keyword emits**:

- the field is declared with a **required custom modifier** `modreq([System.Runtime.CompilerServices.IsVolatile)`
  on its type — this is what makes the JIT treat *every* access to the field as volatile (acquire on
  load, release on store), and
- every backing-field load/store additionally carries the **`volatile.` IL prefix** (`ldfld`/`stfld`,
  `ldsfld`/`stsfld`), matching C# codegen belt-and-suspenders.

kotc recognizes `@kotlin.concurrent.Volatile` as a plain Kotlin-language fact (like `suspend` /
`@Synchronized` — it is a normal Kotlin annotation, **not** a `@Clr*` binding) and emits a
`"volatile":true` field flag; ilemit applies the modreq + prefix. Matches Kotlin/JVM semantics: only
**backing-field** operations are volatile — a property getter/setter doing several field operations is
not atomic as a whole. (The single-threaded test gate proves functional correctness of reads/writes;
the cross-thread memory-visibility guarantee rests on the modreq being precisely the C# `volatile`
encoding, which the JIT honors and which a single-threaded run cannot itself observe.)

## 5. Primitive stringification is CLR-native (not Kotlin/JVM cosmetics)

- Kotlin does **not specify** the string form of `Boolean`/`Double` (only the *source literals* are `true`/`false`);
  `toString()`/`println` rendering is unspecified-behavior. A DotKt program IS a .NET program, so it picks the
  **.NET-native** rendering: `println(true)` → `True` (not the JVM's `true`), `println(4.0)` → `4` (not `4.0`). This
  is a *choice within unspecified behavior*, not a deviation from the Kotlin language — the JVM's `true`/`4.0` are
  themselves just `java.lang.Boolean`/`Double.toString` implementation details, not language essence.
- NUnit tests assert the chosen CLR-native forms directly.
- Memory `clr-native-primitive-formatting`.
- **`String.format` exists on DotKt as platform API (like the JVM has its own; Native/JS have none), but uses the
  .NET composite format (`"{0} items"`, `"{0:D5}"`, `"{0,-4}"`), not Java printf (`"%d"`)** — the same
  host-convention family as the stringification above. Both shapes are provided (`String.format(fmt, args...)`
  and `"fmt".format(args...)`), bound to `System.String.Format` (stdlib `@ClrIntrinsic`, no compiler lowering).
- **`Any.toString()`'s DEFAULT rendering (no user override) is the bare .NET type name, not the JVM's
  `Type@hexhash` identity form.** `class Box(val n: Int)` (no `toString` override, not a `data class`)
  → `println("[$bx]")` prints `[Box]` (namespace-qualified for a packaged class, e.g. `pkg.Box`),
  where Kotlin/JVM prints `[Box@1b6d3586]` (`getClass().getName() + '@' + Integer.toHexString(hashCode())`).
  kotc never emits a `toString` method for a class that doesn't declare one, so the call routes
  through the `objMethod`/virtual-dispatch path straight to the inherited **`System.Object.ToString()`**
  slot (`BirEmitterCalls.kt:1190-1214` — `declaresOwn` is `false`, so the call is NOT short-circuited
  and falls through to the plain virtual `toString` `objMethod`, which bir2cir/ilemit resolve to the
  .NET base slot), and `Object.ToString()`'s default body is the type name — it has no per-instance
  hash component. Consequence: two distinct `Box` instances with no override render **identically**
  (`[Box]` / `[Box]`), where JVM's default form would visibly differ by hash. Same class of
  unspecified-behavior host-convention divergence as the rest of this section — accepted, document only.

## 5a. `Double`/`Float` boxed structural equality & `compareTo` follow Kotlin's total order

Kotlin gives `Double`/`Float` a **total order** in the boxed / `compareTo` / structural-`equals` path (distinct from the
primitive `==`/`<` operators, which stay IEEE): `-0.0 != 0.0` structurally, `(-0.0).compareTo(0.0) == -1`, `NaN` is the
largest value with `NaN == NaN` structurally and `NaN.compareTo(NaN) == 0`. On the CLR `kotlin.Double` **IS**
`System.Double` (no distinct Kotlin wrapper), whose `Object.Equals`/`CompareTo` do NOT match that order
(`(-0.0).Equals(0.0)` is `true`, `(-0.0).CompareTo(0.0)` is `0`). DotKt matches Kotlin (final-review C14, 2026-07-06):

- A **boxed** `==` (`kotlin.Any.equals` on a boxed floating value — e.g. `(-0.0 as Any) == (0.0 as Any)`) — AND an
  **explicit `.equals()`** method call on a boxed floating value (`(-0.0).equals(0.0)`) — are routed by kotc to the
  stdlib total-order helper `clrDoubleEquals`/`clrFloatEquals` (`toBits()` bit-compare, NaN-canonicalized):
  `(-0.0 as Any) == (0.0 as Any)` and `(-0.0).equals(0.0)` → **`false`**, `(NaN as Any) == (NaN as Any)` and
  `Double.NaN.equals(Double.NaN)` → **`true`**.
- A direct `Double`/`Float.compareTo` is routed to `clrDoubleCompare`/`clrFloatCompare` (JDK total-order algorithm):
  `(-0.0).compareTo(0.0)` → **`-1`**, `Double.NaN.compareTo(1.0)` → **`1`**, `Double.NaN.compareTo(Double.NaN)` → **`0`**.

- A **collection** `.equals()` (List/Set/Map) is likewise routed to the stdlib structural helper, exactly like the
  `==` operator (`listOf(1,2).equals(listOf(1,2))` → **`true`**), while a **plain object** `.equals()` keeps
  `Object.Equals` reference identity. String `.equals()` uses String's own value-equality binding.

The **primitive** operators stay IEEE (matching Kotlin, and `il-nancmp`-green): `-0.0 == 0.0` → `true`,
`Double.NaN == Double.NaN` → `false`, and direct `<`/`>`/`<=`/`>=` (which desugar to the IEEE compare intrinsics, not
`.compareTo`) are unaffected. Coverage: `tests/basic/fixtures/FloatTests.kt`,
`tests/basic/fixtures/CollectionsTests.kt`, and `tests/basic/fixtures/InterfaceDslAndRuntimeTests.kt`.

## 5a-bis. Referential identity `===` on primitive/boxed/enum values deviates from Kotlin/JVM

`===` (`EQEQEQ`) lowers **unconditionally** to `binOp ==` → IL `ceq`, with **no representation
check** (`PrimitiveOperatorLowering.cs:221-223`) — unlike structural `==`/`.equals()` (§5a), which
routes through type-classifying helpers. Because CLR generics are **reified** (§2) and a basic
`enum class` is a real CLR value-type `enum` (`BirEmitter.kt:514-515`), that single `ceq` lowering
produces three JVM-diverging outcomes:

- **A generic type parameter instantiated over a primitive compares by VALUE, not identity.**
  `fun <T> ident(a: T, b: T) = a === b; ident(1000, 1000)` → **`true`** on DotKt. At the CLR the
  generic method is JIT-specialized per value-type instantiation, so `T = Int` is an unboxed
  `System.Int32`; `ceq` on the raw values IS a value compare. On the JVM, `T` erases to `Object`, so
  `a`/`b` are two separately autoboxed `Integer`s outside the `-128..127` cache → **`false`**.
- **A boxed primitive has no identity cache.** `val a: Any = 1; val b: Any = 1; a === b` → **`false`**
  on DotKt (each widening to `Any` boxes fresh; `ceq` is a genuine reference compare here), where the
  JVM returns **`true`** for values in `-128..127` (`Integer.valueOf` cache). DotKt has no such
  cache — arguably more principled, since Kotlin never *guarantees* boxed-primitive identity, but the
  observed result differs.
- **A boxed enum loses its singleton identity — this one breaks a Kotlin guarantee, not just an
  unspecified boxing detail.** A basic `enum class` lowers to a real CLR value-type `enum`
  (`BirEmitter.kt:514-515`); widening it to `Any` boxes a **fresh** object each time. `val e1: Any =
  Color.RED; val e2: Any = Color.RED; e1 === e2` → **`false`** on DotKt, whereas Kotlin/JVM enum
  entries are singletons and `===` is **always `true`**, boxed or not. A *directly*-typed compare
  (`Color.RED === Color.RED`, no widening to `Any`) stays a value-type `ceq` on the same constant and
  is correctly `true` — only the `Any`-boxed path diverges.

Accepted deviation (no code fix planned) — record only.

## 5a-ter. `Double/Float.roundToInt`/`roundToLong` round half-up via `floor(x + 0.5)` — two pathological ulp deviations from JVM

`roundToInt`/`roundToLong` implement Kotlin's contract "ties are rounded towards positive infinity" as
`floor(x + 0.5)` (`0.5 → 1`, `2.5 → 3`, `-2.5 → -2`, `-0.5 → 0`; NaN throws `IllegalArgumentException`; out of
`Int`/`Long` range saturates to `MIN`/`MAX`). `kotlin.math.round` itself stays ties-to-**even** (`System.Math.Round`),
matching Kotlin. `Float` rounds through `Double` (lossless), so it is EXACT for every float input.

Kotlin/JVM's `Double.roundToInt`/`roundToLong` delegate to `java.lang.Math.round`, whose bit-manipulation avoids two
sub-ulp cases that a `floor(x + 0.5)` formulation hits — the only inputs where the CLR result differs:
- **`0.49999999999999994` (the largest `double` < 0.5)** → CLR `1`, JVM `0` (the `+ 0.5` addition rounds up to `1.0`).
- **odd integral doubles in `[2^52 + 1, 2^53)`** (in `Int`/`Long` range, so not clamped) → CLR is off by one for
  `roundToLong` (`x + 0.5` rounds to `x + 1` under ties-to-even).

Both are pathological (no realistic numeric code rounds these) and are precedented on non-JVM Kotlin backends.
Coverage: `tests/basic/fixtures/MathTests.kt`.

## 5b. `CharSequence` is `string` on the CLR — an immutable snapshot, not a live view

`kotlin.CharSequence` is a JVM-shaped polymorphic char view with **no faithful .NET equivalent** (`System.String` is
sealed; `System.Text.StringBuilder` shares no indexed-char interface with it; `ReadOnlySpan<char>` — the only real .NET
"char view" — is a `ref struct` that cannot be a general parameter/field/generic-arg type). Per the project philosophy
(*Kotlin carries JVM accidental complexity; on the CLR, identify and discard it*), DotKt **models `CharSequence` as
`System.String`** at the CLR boundary. Consequences (a deliberate, declared deviation — like CLR-native
stringification above):

- A `CharSequence`-typed **parameter / return / local** is emitted as `System.String`; its member reads
  (`length` / `get`/`[]` / `subSequence`) resolve to `System.String.Length` / `get_Chars` / `Substring`. In C# a
  DotKt `fun f(cs: CharSequence)` therefore surfaces as `void f(string cs)` — clean interop, no synthetic type.
- A **non-`String` `CharSequence` value** (a `StringBuilder`, any other char sequence) flowing into a `CharSequence`
  (= `string`) slot is coerced with an **implicit `.toString()` snapshot** at the boundary. A `String` flows directly.
- Therefore `CharSequence` has **`string` (immutable-snapshot) semantics** — the JVM "live view" (mutating a
  `StringBuilder` after passing it as a `CharSequence` and observing the change through the parameter) is
  **NOT supported**. It is honest because it is declared, not hidden.
- The one construct that keeps a synthetic interface: a user-declared **`class S : CharSequence`**. Sealed
  `System.String` cannot be a supertype, so such a class implements a synthetic monomorphic `dotkt$CharSequence`
  interface, and an assembly that declares one keeps `CharSequence` polymorphic assembly-wide (so a
  `show(cs: CharSequence) = cs.length` still dispatches to the user impl). Passing a user `S` into a *different*
  assembly's `CharSequence` (= `string`) slot still snapshots it via `.toString()`.
- Design + layer plan: `docs/design-charsequence-clr-string.md`. Implemented in bir2cir (`CharSeqStringLowering`,
  app builds without a user implementer). The **stdlib's own** CharSequence-extension signatures are not yet lowered to
  `string` (a follow-up needing a stdlib rebuild); they still route through the `dotkt$CharSequence` adapter bridge.
- The **`dotkt$CharSequence` adapter bridge** (a `String`/`StringBuilder` value flowing into a stdlib
  CharSequence-extension slot; `StringCharSequenceBridge`) shares this snapshot semantics: a `StringBuilder` receiver
  is `.toString()`-snapshot into the adapter, so a `val cs: CharSequence = sb` initialized from a StringBuilder (or a
  StringBuilder returned as `CharSequence`) captures the content at that point — a later `sb.append(...)` is NOT
  observed through `cs`. Same immutable-snapshot rule as the `= string` model above, applied at the adapter boundary.

## 5b-bis. `Regex.matchEntire`/`matches` are a TRUE anchored full match (matches Kotlin/JVM)

`Regex` is `@ClrTypeAlias` → `System.Text.RegularExpressions.Regex`, but a leftmost `Regex.Match` (a SEARCH accepting
the FIRST result) is NOT a full match: `Regex("a|ab").matchEntire("ab")` finds `a` first, and a naive
"first-match-must-span-the-input" filter then wrongly returns `null`. DotKt instead anchors the ENGINE (#162):
`matchEntire` re-matches the pattern wrapped as `\A(?:<pattern>)\z` — the non-capturing group scopes a top-level
alternation and preserves the user's capture-group NUMBERS — with the instance's OWN compiled `RegexOptions`, so the
engine backtracks to a full-input match when one exists (alternation branch order, lazy quantifiers, `(?i)` options,
and coexisting `^…$` anchors all behave as Kotlin/JVM). `matches` delegates to `matchEntire != null`.

## 5b-quater. `Regex.options` decodes the compiled `RegexOptions`; three `RegexOption`s have no CLR bit (deliberate CLR choice)

`Regex.options` reads the instance's compiled `System.Text.RegularExpressions.RegexOptions` `[Flags]` bitmask (the enum
reduces to its `Int32` value) and decodes the four flags that have a Kotlin counterpart: `IgnoreCase`→`IGNORE_CASE`,
`Multiline`→`MULTILINE`, `Singleline`→`DOT_MATCHES_ALL`, `IgnorePatternWhitespace`→`COMMENTS`. The remaining three
Kotlin `RegexOption`s have **no** `System...RegexOptions` bit and therefore never round-trip (deliberate CLR choice —
they are unrepresentable in a compiled .NET `Regex`): `LITERAL` (.NET realizes literal matching via `Regex.Escape`, not
an option), `UNIX_LINES` (no .NET line-terminator mode), and `CANON_EQ` (no .NET canonical-equivalence flag). A default
`Regex` decodes to an empty set. The inverse — the `Regex(pattern, options)` constructor's `Set<RegexOption>` /
`RegexOption` → `RegexOptions` encode — is wired in bir2cir `NetInteropBinding` (#178) and mirrors this table
(`IGNORE_CASE`→1, `MULTILINE`→2, `DOT_MATCHES_ALL`→16, `COMMENTS`→32). Symmetrically, the three unrepresentable options
**encode to no bit (dropped)**: e.g. `Regex("a.b", RegexOption.LITERAL)` compiles with `RegexOptions.None`, so `.`
matches as a wildcard rather than a literal (a deliberate CLR choice, consistent with the decode table above — a
program needing literal matching uses `Regex.escape`).

## 5b-ter. Case mapping is CLR one-to-one — `"ß".uppercase()` stays `"ß"` (interop-first deviation)

- Kotlin's KDoc for `uppercase()` specifies the **Unicode standard full case mapping**, under which one-to-many
  expansions apply: `"ß".uppercase()` → `"SS"` on Kotlin/JVM (and per the KDoc letter).
- DotKt binds case mapping to the BCL (`ToUpperInvariant`/`ToLowerInvariant`), which is strictly **one-to-one**:
  `"ß".uppercase()` → `"ß"`, and `Char.uppercase()` never expands to a multi-char string (#144,
  `libraries/stdlib/clr/kotlin/text/CharClr.kt`).
- **Why this deviates even from the KDoc letter** — the ③ *interop-first* case of the acceptance test: a DotKt
  string IS a .NET string, and every mscorlib API the program interops with (`String.ToUpper`, comparers, the
  whole .NET ecosystem) uses the one-to-one mapping. Honoring the KDoc here would make Kotlin-side case mapping
  disagree with the CLR world around it — the *less* consistent, harder-to-explain behavior. The deviation is
  consistent (mscorlib-general), documented (here), and convincingly explainable — it passes the test.

## 5c. `Map`/`MutableMap` BOTH erase to `IDictionary<K,V>` — read-only-ness is frontend-enforced

Kotlin's `MutableMap : Map` subtype relation does **not** exist between the BCL's dictionary interfaces
(`IDictionary<K,V>` does not extend `IReadOnlyDictionary<K,V>`), so the List-style split alias
(`Map→IReadOnlyDictionary` + `MutableMap→IDictionary`) would make every `MutableMap`-value-into-`Map`-slot store
formally unverifiable — on the hot path (`Map.get` on a mutable receiver, `associateTo`'s `M : MutableMap`). DotKt
therefore aliases **BOTH `Map` and `MutableMap` to `System.Collections.Generic.IDictionary`** — exactly Kotlin/JVM's
own model (both erase to `java.util.Map`), and the in-repo precedent of `Iterable`/`MutableIterable` → `IEnumerable`.
Consequences (deliberate, declared):

- **A Kotlin `Map` surfaces to C# as a mutable `IDictionary<K,V>`** (concrete values are `Dictionary<K,V>`); Kotlin's
  read-only-ness is enforced by the Kotlin FRONTEND only, not the CLR type. `emptyMap()` returns a fresh
  Dictionary-backed map (the pure-Kotlin `EmptyMap` singleton cannot satisfy the IDictionary surface).
- **`Map.get` is null-on-missing** (Kotlin semantics), synthesized as `ContainsKey` + `get_Item` in
  `kotlin.collections.ClrMapDefaults` (`IDictionary`'s raw indexer throws); `put`/`remove` return the previous value
  the same way. `size`/`containsKey`/`clear` bind 1:1 (`Count`/`ContainsKey`/`Clear`).
- **`Map.keys`/`values`/`entries` are SNAPSHOTS, not live views** (Kotlin's are live): `keys`/`entries` return a
  pure-Kotlin `Set` (the BCL `KeyCollection` cannot implement the unaliased `kotlin.collections.Set`), `values` a
  BCL List. Entry VALUES are live (`entry.value`/`setValue` read/write through the backing map), but a key
  added/removed after taking the view is not reflected in it. `MutableMap.keys`/`values` bind directly to
  `IDictionary.Keys`/`.Values` (their `MutableSet`/`MutableCollection` slots lower to `ICollection`, which
  KeyCollection/ValueCollection implement); mutating THOSE views does not write back either (BCL contract: they throw).
- **`MutableMap.iterator()` degrades to the `Map.iterator()` shape**: both extensions collapse to the same lowered
  signature (same receiver type), and duplicate `(name, params)` overloads keep only the FIRST under the clean name
  (the second is `$dupN`-mangled). Destructuring `for ((k,v) in m)` works for both; calling `remove()` on the
  iterator, or `setValue` through the *mutable* entry-set element of a `Map`-typed receiver, is where the edges are.
- A **user class implementing `Map`/`MutableMap` in pure Kotlin** must satisfy the full `IDictionary` surface; today
  only the `@ClrIntrinsic`-renamed slots are generated, so such classes (stdlib `AbstractMap`, `MapWithDefaultImpl`)
  fail to LOAD when touched — the known under-tested pure-Kotlin dual-rep path (`dual-representation-stdlib-types`).
- **Map delegation (`val name by data`) requires a String-KEYED map at runtime.** The stdlib
  `Map<in String, V>.getValue` body pins `getOrImplicitDefault`'s K to `String` (a `(this as Map<String, V>)` CLR
  adaptation in `MapAccessors.kt`): the frontend approximates the contravariant captured K to `Any`, which under
  REIFIED generics dispatched `IDictionary<object,V>.ContainsKey` on a `Dictionary<string,V>` →
  `EntryPointNotFoundException`. Consequence: delegating through a `Map<Any, V>` receiver (legal for `Map<in
  String, V>` and fine under JVM erasure) `castclass`-fails on the CLR, because that value is an
  `IDictionary<object,V>`. Use a `Map<String, …>`-typed map for `by`-delegation.

## 5c-ter. `LinkedHashMap`/`LinkedHashSet` (and `mapOf`/`setOf`) DO preserve insertion order — matching Kotlin/JVM

Kotlin CONTRACTS insertion-order iteration for `LinkedHashMap`/`LinkedHashSet`, and `mapOf`/`mutableMapOf`/`setOf`/
`mutableSetOf` return those `LinkedHash*` types — so they are insertion-ordered too. Naively aliasing them to .NET
`Dictionary<K,V>`/`HashSet<E>` broke this: those preserve insertion order only INCIDENTALLY and lose it after a
removal (no guarantee). DotKt binds them to insertion-ordered containers instead (#169):

- **`LinkedHashMap<K,V>` is `@ClrTypeAlias` → `System.Collections.Generic.OrderedDictionary<K,V>`** (.NET 9+), which
  keeps insertion order across removals and exposes the same non-generic `IDictionary`/`ICollection` facades and
  intrinsic members (`Count`/`ContainsKey`/`ContainsValue`/indexer/`Remove`/`Clear`/`Keys`/`Values`) the
  `ClrMapDefaults` helpers rely on — a transparent swap for all of §5c's map behavior, now ordered.
- **`LinkedHashSet<E>` is a pure-Kotlin `MutableSet`** backed by that `LinkedHashMap` (exactly as Kotlin/JVM backs it
  with a `LinkedHashMap`), since .NET has no ordered GENERIC set. It gets the `CollectionBclSlotSynthesis` `ICollection`
  slots + the reverse `GetEnumerator` bridge, so it flows through `Set`/`MutableSet` slots like any BCL set.
- **Plain `HashMap`/`HashSet` stay UNORDERED** (`Dictionary`/`HashSet`) — Kotlin contracts no order for them.

## 5c-bis. Nested collection type-arguments collapse to their INVARIANT CLR sibling (`List`→`IList` at depth ≥ 1)

§5c's head-position Map collapse has a general cause: CLR generics are **invariant**, and the read-only
interface does not derive from its mutable sibling (`IList<T>` does **not** inherit `IReadOnlyList<T>`;
`ICollection<T>` not `IReadOnlyCollection<T>`). At the top level (**head**) DotKt keeps the covariant read-only
alias — so `val xs: List<Number> = listOfInts` stays verifiable via CLR interface covariance. But **inside a
generic type argument** the covariance is unusable: `groupBy` returns a concrete `Dictionary<K, List<V>>` (a
*mutable* list in the value slot), which inhabits no instantiation of a `Map<K, List<V>>` slot lowered with the
read-only sibling in the value position (invariant `IDictionary<K, IReadOnlyList<V>>`). So bir2cir **collapses
each read-only collection FQN to its invariant sibling whenever it appears at generic-argument depth ≥ 1**:
`List`→`IList`, `Collection`/`Set`→`ICollection` inside any type argument (and in `newList`/`newMap`/`newSet`
element keys and call/ctor type-args); the head keeps the covariant alias. Then `Map<K, List<V>>` lowers to
`IDictionary<K, IList<V>>` and the concrete `Dictionary` inhabits it. Where a head-position read-only value then
meets a collapsed mutable slot (or vice-versa), ilemit reconciles with a runtime-checked `castclass` (always
verifiable — a closed interface cast — and succeeds because stdlib collection values implement every face).

Known deliberate gaps (all **verify-only / run-correct** for stdlib-backed values, tracked as follow-ups):
- A **user class implementing ONLY the read-only face** (`class X : List<T>` with no mutable sibling) cannot be
  stored into a nested collapsed `IList` slot — the `castclass` throws at runtime. stdlib/BCL collections
  implement all faces, so this bites only hand-rolled read-only-only user collections.
- A **foreign C#-supplied `IList`-only collection** flowing into a read-only slot likewise throws at the
  reconciling `castclass` (interop collections are outside the current stdlib-value assumption).
- A **nested covariant upcast** (`val b: List<List<Any>> = a` where `a: List<List<String>>`) is verify-only
  dirty — the collapse trades the (previously CLR-granted, rarely-used) nested covariance for the far more
  common concrete-into-slot verifiability. It runs correctly.

## 5c-ter. Two residual covariance gaps left DURABLE after the Root-V collapse (accepted, not fixed)

The #75/#100 Root-V collapse (§5c-bis) closed nested **value**-covariance for `List`/`Collection`/`Set`. Two
covariance seams it did **not** address are deliberately accepted as durable limitations (task #102) — both are
narrow, and closing either would add per-call reconciliation for an idiom that is rare or already run-correct.

- **`Map<out K, V>` key-covariance (the "Root-K" seam).** `Map` is declared `Map<K, out V>`: values are
  declaration-site covariant (and collapse via §5c-bis), but **keys are invariant**, exactly as CLR
  `IDictionary<K,V>` (§5c) is. A *use-site key projection* `Map<out K, V>` therefore appears in the stdlib's
  copy/merge signatures — `MutableMap.putAll(from: Map<out K, V>)`, `Map<K,V> + Map<out K, V>`,
  `HashMap(src)` — and lets a source map with a **narrower key type** feed a wider-`K` destination. On the CLR that
  is `IDictionary<Dog,V>` flowing into an `IDictionary<Animal,V>` slot, which the invariant generic cannot express.
  bir2cir's `MapVarianceRealign` already **undoes the frontend's `in`/`out` → `kotlin.Any` over-approximation**
  (restoring the concrete type inside inlined stdlib bodies, and the star-projected `Map<*,*>` `get`/`containsKey`
  route to the non-generic `IDictionary` facade — §5c) so the *common* case (identical key type, or value widening)
  is verifiable and run-correct. What stays open is only the case where a user **genuinely widens the KEY type**
  across a `putAll`/`plus`/copy-ctor boundary. Reachability: uncommon but not exotic — you hit it only by merging a
  `Map<Sub, V>` into a `Map<Super, V>`-typed target; same-key merges (the overwhelming majority) never touch it.
  **Disposition: documented, not fixed** — key invariance matches CLR `IDictionary`, so there is no covariant
  sibling to collapse to; use a target-key-typed source map when merging.
- **The ~46 internal `IList`↔`IReadOnlyList` view seams inside the shipped runtime stdlib (`DotKt.Stdlib.dll`).**
  These are the head-vs-nested face mismatches (§5c-bis) as they occur **inside stdlib bodies** — a head-position
  read-only value meeting a collapsed mutable slot, or its exact transpose. They are **reconciled at emit** by
  ilemit's `IsCollectionViewSeam` `castclass` (bidirectional since #100 H1) and are **not user-observable**: every
  stdlib/BCL-backed collection implements all faces, so the closed-interface cast always succeeds. The only way to
  surface an `InvalidCastException` is the already-documented §5c-bis edge — a hand-rolled **read-only-only** user
  collection or a foreign **`IList`-only** C# collection crossing such a slot. **Disposition: internal + reconciled,
  documented, not fixed** — they are body-level emit artifacts, not an exported ABI shape.

## 5c-quater. Cross-module collection surfacing: DotKt `kotlin.collections.*` restores; genuine C# BCL stays BCL (#27)

When a Kotlin program consumes a **referenced assembly** as Kotlin (a `<ProjectReference>` / `<Reference>` to a
DotKt library, or a façade-free `import` of a .NET type), facadegen reads each member signature's .NET types and
maps them to Kotlin tokens. The BCL collection interfaces the forward `@ClrTypeAlias` table emits are **reverse-mapped
back** to `kotlin.collections.*` — but **only for a DotKt-emitted library**, detected by the conjunction of the
assembly-level `[AssemblyMetadata("DotKt.Compiler", "metadata-v1")]` marker and a compiler-generated embedded
`DotKt.Runtime.CompilerServices.KotlinFileClassAttribute` carrier (`IsDotKtEmittedAssembly`). A matching namespace or
attribute full name without those provenance markers is ignored. Consequences:

- **A DotKt library's `fun f(xs: List<String>)`** compiled its param to `IReadOnlyList<String>`; a consumer's
  `listOf(...)` (a `kotlin.collections.List`) now **unifies** with it, and generic inference / element-member
  resolution (`h.items.size`) work exactly as same-module. The reverse targets are the inverse of the forward table:
  `IReadOnlyList→List`, `IList→MutableList`, `IReadOnlyCollection→Collection`, `ICollection→MutableCollection`,
  `IEnumerable→Iterable`, `IDictionary→Map`. Where the forward map is many-to-one (`{Collection,Set}→IReadOnlyCollection`;
  `{Map,MutableMap}→IDictionary`, §5c) the inverse picks the read-only **supertype** — the most permissive param type
  (a `Map`-typed param accepts both `mapOf(...)` and `mutableMapOf(...)`).
- **A genuine C# assembly's `IReadOnlyList<T>` / `IList<T>` / `IEnumerable<T>` stays a BCL interface** — it was never a
  Kotlin `List`, and it has a DIFFERENT member surface (`.Count`/`.Add`/`.IndexOf` vs Kotlin `.size`/`.add`), so
  façade-free interop keeps direct BCL member access. This is why the reverse map is DotKt-gated and **not** universal
  like `System.Int32→kotlin.Int` (which is safe universally because the CLR type and member surface are identical).
- **Deliberate lossiness (accepted).** Because the forward map is many-to-one, the reverse cannot recover the original
  in the collapsed families: a DotKt `fun g(m: MutableMap<K,V>)` surfaces cross-module as a `Map<K,V>` param (and a
  `MutableMap`/`Set`/`MutableSet` **return** surfaces widened to `Map`/`Collection`/`MutableCollection`). This mirrors
  Kotlin/JVM's own `MutableMap`→`java.util.Map` erasure hole; the frontend's read-only mutability gate is not
  reconstructed cross-module. **Disposition: documented, not fixed** — the exact restore would need a per-signature
  round-trip stamp of the original Kotlin collection identity (a bir2cir `RoundtripMetadata` follow-up). `List`,
  `MutableList`, and `Map` — the common cases — round-trip precisely.

## 5d. `Appendable` is `System.Text.StringBuilder`

`kotlin.text.Appendable` is a JVM-ism (`java.lang.Appendable`) with **no distinct .NET representation** —
`StringBuilder` is the CLR's sole general appendable char sink. Mirroring the `CharSequence`→`string` collapse (§5b),
DotKt aliases `Appendable` to `System.Text.StringBuilder` (`@ClrTypeAlias` + `@ClrIntrinsic("Append")` on
`append(Char)`/`append(CharSequence?)`). Consequences:

- An `Appendable`-typed parameter / return / bound (`<A : Appendable>`) surfaces to C# as `StringBuilder` — this is
  what makes `joinTo(StringBuilder(), …)`-style stdlib generics verifiable on the CLR.
- A **user class implementing `Appendable`** is therefore NOT supported (you cannot subclass the sealed-in-practice
  role); write to a `StringBuilder` instead. This is narrower than the JVM, and deliberate.

## 5e. Enum classes have two CLR shapes

- A **basic** `enum class` (constants only, no ctor params / methods / per-entry bodies) → a **real CLR `enum`** —
  ideal for .NET interop (usable in C# `switch`, attributes, etc.).
- A **rich** enum (constructor params, methods, per-entry bodies) → a **singleton-field class** (one static readonly
  instance per entry, with real properties/methods; `name`/`ordinal`/`values()`/`valueOf()` synthesized).
- Within a DotKt module both behave like Kotlin enums. **Across the round-trip** (re-consuming the dll as Kotlin)
  neither is restored as a Kotlin `enum class` — see §10.2 — so exhaustive `when` over a *consumed* enum degrades.
  A facadegen-imported **.NET** enum arrives as an object of enum-typed `val`s (read, pass, `==`, `when` all work,
  without exhaustiveness).

## 5f. `value class` is a real wrapper class — never erased

The OPPOSITE of Kotlin/JVM: a `@JvmInline value class Money(val amount: Int)` is emitted as an **ordinary reference
class** (an ordinary accessor-routed property — §5h — plus synthesized `equals`/`hashCode`/`toString`) — no inline-class erasure, no
mangled `-impl` statics, no .NET `struct`. Structural equality survives; what is lost is the value-ness itself
(identityless-ness is not enforced). The frontend still *requires* the `@JvmInline` annotation (a pinned-frontend
checker); the attribute itself is not emitted. See §10.3 for the round-trip view.

## 5g. `KClass.simpleName`/`qualifiedName` report Kotlin names for the statically-known `::class` case (#138)

`T::class` binds `kotlin.reflect.KClass` onto `System.Type` (intensionally faithful: `Foo::class`→`typeof` = an UNBOUND
`classRef`; `x::class`→`GetType()` = a BOUND `getType`, reflecting the value's runtime class). **Kotlin contract
(restored):** the NAME accessors report the Kotlin name — not the .NET reflection name — whenever the receiver's Kotlin
type is **statically known**. bir2cir `KClassMemberBinding` runs BEFORE `BirTypeLowering`, so the receiver's type slot is
still a pure Kotlin FQN token (`kotlin.Int`, `kotlin.String`, a user FQN); the pass **const-folds** the accessor straight
off it — `qualifiedName` = the FQN verbatim, `simpleName` = its last `.`-segment. No CLR→Kotlin reverse table is needed:
the Kotlin identity is still in hand. It folds two cases:

- **Unbound `classRef`** (`Int::class`, `Foo::class`, a generic `Box::class`, a reified `T::class` after inline-splice):
  always foldable — the token IS the literal type. `Box::class.simpleName` = `"Box"` (class literals drop type args).
- **Bound `getType`** on a **known-final builtin** — the primitive tower + `String` (`1::class`, `"x"::class`). A final
  type has no subtypes, so the runtime class == the static type carried on the argument's `sty`/`type` slot; the fold is
  sound (and gated to a side-effect-free `const`/`local` receiver so no evaluation is dropped). `1::class.simpleName` =
  `"Int"`; `1::class.qualifiedName` = `"kotlin.Int"`; `"x"::class.qualifiedName` = `"kotlin.String"`.

The old **backtick-mangled** (`IList\`1`) and `null` outputs are gone for these statically-known cases: they arose only
from reading `Type.Name`/`.FullName` at run time, which the const-fold no longer does.

**Framing:** Kotlin-contract restoration — `KClass.simpleName`/`qualifiedName` carry the Kotlin KDoc contract ("the name
as declared in source code"), so reporting the Kotlin name (not `Int32`/`System.Int32`) honors the contract by default.

**Still dynamic (sequenced follow-up):** a BOUND `getType` still reads through `System.Type.Name`/`.FullName` — so can
surface a .NET reflection name / backtick mangling / the CLR `+` nested separator — in these cases the const-fold does
not reach:

- static type is **open/an interface** (`x: Any`, a `List<Int>` value): the runtime class is a subtype, genuinely
  dynamic.
- a **generic** or **nested** final **user** class (`box::class` where `box: Box<Int>`, `inner::class` where
  `inner: Outer.Inner`): reads back `Box\`1` / `Outer+Inner`. (A NON-generic TOP-LEVEL final user class IS already
  correct via the run-time read — such a type is not CLR-renamed, so `Type.FullName`/`.Name` are its Kotlin
  qualified/simple name. The UNBOUND `Box::class`/`Outer.Inner::class` is exact via the classRef fold regardless.)
- the receiver is a **smart-cast wrapper** (`val a: Any = 1; if (a is Int) a::class`) or a bare **`this::class`** in a
  non-inline member — kotc emits a `cast`/`nullableValue`/`this` node, not the `const`/`local` the fold gates on (a
  `cast` node can also carry a throwing explicit `as`, so it is not safe to fold away).

Closing these needs a small **runtime CLR→Kotlin reverse-map helper** (a stdlib runtime function `KClassMemberBinding`
routes the dynamic `getType` to) — a cross-layer stdlib piece, not a bir2cir-only const-fold. It does **not** belong in
kotc or ilemit.

## 5h. An auto-property's backing field is a compiler-generated `<Name>k__BackingField` (#228)

An **accessor-routed** Kotlin property becomes a real CLR property (`Name` + `get_Name`/`set_Name`); its storage is
emitted as an assembly-visible (`internal`) field named **`<Name>k__BackingField`**, stamped
`[System.Runtime.CompilerServices.CompilerGenerated]` — the same convention and attribute `csc` uses for a C#
auto-property. Kotlin/JVM instead names the field after the property, which on the CLR would put a property and a field
of the SAME name on one type: reflection-driven .NET libraries group candidate members by name and cannot resolve that
pair (Newtonsoft's `SerializeObject` silently returned `{}`, and the round-trip back threw).

**Framing:** interop-first, CLR-native. The name is un-writable in Kotlin: even backtick-quoted, the frontend rejects
``var `<Value>k__BackingField` = 0`` with *"name contains illegal characters: <>"* — so it can never collide with, or be
referenced by, a user declaration; and it is derived from the property name, so two properties never share one. The
rename lives in `bir2cir` (the layer that owns the Kotlin↔CLR representation); kotc keeps emitting the pure Kotlin
identity.

"Accessor-routed" is the exact scope, and it is **wider than "default accessors"**: a property that has a backing field
*and* a custom accessor (`val p = 7; get() = field + 1`) still emits a CLR property, so its storage is renamed too.
Only a property whose storage **is** the user-visible member emits no CLR property and therefore keeps its plain field
name: `lateinit var`, `const`, a delegated property's `p$delegate`, a companion/top-level `val`/`var` (a static field),
and the `@ClrField` opt-out (§5f-adjacent: `@ClrField` deliberately emits a plain public field instead of a property).

## 6. Consuming a DotKt assembly AS KOTLIN — what rides metadata vs. needs an attribute

When another `.ktproj` consumes a DotKt assembly, the Kotlin facts with **no native .NET representation** are carried
by `DotKt.Runtime.CompilerServices` attributes and restored on the consumer's FIR; the rest round-trips through plain
.NET metadata. Those attributes are **compiler-EMBEDDED** into each emitted assembly (internal types, like csc's own
`NullableAttribute`/`IsReadOnlyAttribute`) — they are metadata-only, never executed, so they don't live in a referenced
runtime.

| Kotlin construct | carrier |
|---|---|
| `infix` / `operator` | `[KotlinFunction(Infix\|Operator)]` |
| `suspend` (a `suspend fun`) | `[KotlinFunction(Suspend)]` (+ `Task<T>`→`T` unwrap) |
| a `suspend (…) -> T` **function TYPE** (parameter / return / property / field) | `[KotlinSuspendFunctionType("sfunc:<ret>:<args>")]` preserves the pre-erasure shape because the CLR slot itself erases to `object`. bir2cir records it, ilemit stamps it, facadegen reads it, and kotc restores `kotlin.coroutines.SuspendFunctionN`. All four positions are covered by the roundtrip NUnit suite. |
| top-level functions | `[KotlinFileClass]` on the `<File>Kt` facade → restored as package-level functions. Same-name overloads that live in **different** source files of the same package (`foo()` in `UtilsKt`, `foo(Int)` in `HelpersKt`) each route back to their **own** file-facade class — resolved by the call's arity, so no cross-file mis-routing. |
| `inline` (with a lambda) | `[KotlinInline(birJson)]` (only for cross-module non-local return; see §3) |
| **reference-type nullability** (`String?`) | **.NET's own NRT** `[Nullable]`/`[NullableContext]` (§9) — readable by C# too |
| `final`/`open`/`abstract`, visibility | **none** — ride .NET virtual-ness / accessibility |
| generics, `reified` | **none** — CLR generics are reified (§2) |
| parameter names (named-argument calls) | emitted via `DefineParameter` (were dropped before; not a FIR limitation) |

Deep dive: `docs/design-kotlin-metadata-attributes.md`.

## 7. Default arguments — a two-tier rule (native metadata, else a carried BIR expression)

Kotlin's default arguments are semantically **callee-side** (the default expression is evaluated inside the function, in
its scope) — Kotlin/JVM implements this with a synthetic `f$default(…, mask)` method. DotKt has **no `$default` synthetic**;
it fills an omitted argument **positionally at the call site**, by one of two mechanisms chosen per-parameter by a single
test — **can the parameter's own CLR type carry its default as a `[DefaultParameterValue]` constant?** Filling is
POSITIONAL: an omitted middle default keeps every later provided argument in its own parameter slot, so
default-omission works **everywhere** — trailing, named-middle, reordered, and mixed with a trailing lambda
(`list.joinToString("-") { … }`, `str.substringAfter("=")`, `p.copy(field = x)`), not only cross-module.

- **Tier 1 — YES (native).** A primitive/char/bool const on its primitive param, a `String` const on a `String` param,
  or a `null` const on any reference/nullable param → the parameter is emitted `[Optional]` + `[DefaultParameterValue(const)]`.
  ilemit's `EmitDefaultArg` fills the omitted arg from that metadata, and **C#/VB/F# consumers get the default natively**.
  Works for named-middle and reordered omission (`greet("C", punct = "?")`, `box(1, c = 9)`, `Pt(y = 4)`).
- **Tier 2 — NO (a carried BIR expression).** The prime cases are a `String` const on a `CharSequence`/interface-typed
  param (a string constant cannot sit in a `[DefaultParameterValue]` on an interface type) and **any non-constant
  default** — a call (`= emptyList()`), an empty lambda (`= {}`, the Avalonia `configure: Panel.() -> Unit = {}` idiom),
  a receiver-reading default (`= this`). Such a parameter is emitted **REQUIRED** for a C# consumer (no `[Optional]`); a
  **kcc** consumer instead sees it OPTIONAL, because facadegen surfaces the `@kotlin.clr.KotlinDefault`-carrying param
  with a `nonConst` default marker so the frontend accepts the omission (#146). The default EXPRESSION is carried as a
  **CLOSED** BIR sub-tree on the `@kotlin.clr.KotlinDefault(index, birJson)` attribute (mirroring `[KotlinInline]`): a
  non-capturing lambda default, whose `newDelegate` would point at a library-LOCAL lifted method, is carried as a
  `{"k":"defaultCarrier","expr":…,"lifted":[…]}` envelope embedding that method so it is self-contained (kotc detaches
  the dead method from the library dll). A capturing / SAM / suspend lambda default, OR a default that reads its
  **enclosing-instance receiver** — a member fn's own dispatch `this@Owner` or an inner-class member's outer
  `this@Outer` (detected by an IR-symbol scan of the dispatch-receiver param + every enclosing class `thisReceiver`) —
  cannot be reconstructed positionally cross-module → a `{"k":"defaultUnsupported"}` poison carrier the consumer's
  splice refuses on (a precise diagnostic, never a miscompile: the one uniform carrier binds `{"k":"this"}` to args[0],
  never to an enclosing instance). An EXTENSION-receiver `= this` is NOT this case — the extension receiver DOES bind to
  args[0], so it round-trips (below). For a CROSS-MODULE call kotc emits a POSITIONAL `{"k":"defaultArg"}` placeholder for each omitted
  arg of such a callee (so a later provided arg keeps its slot), and `bir2cir.DefaultArgSplice` — run at **PHASE 1**
  (right after `InlineSplice`, before owner attribution / the CharSequence bridge / type-lowering, so the spliced RAW
  expression re-lowers in THIS app's context) — resolves the callee OWNERLESSLY (by method name + emitted arity, the
  owner not yet attributed) and replaces each placeholder in place by array index (matching the `@KotlinDefault` stamp
  index), RE-HOISTING a `defaultCarrier`'s lifted method into the consumer's file class under a fresh per-splice name.
  An EXTENSION-receiver `= this` default carries `{"k":"this"}` → the call's receiver (args[0]); a default reading an earlier value param carries
  `{"k":"defaultArgParam","idx":N}` → the call's arg N. For a SAME-MODULE call kotc has the real default IR and inlines
  it directly. A **C#** consumer sees a required parameter and passes it explicitly. A function with ≥1 Tier-2 parameter
  carries `@KotlinDefault` on ALL its defaulted parameters, so a run of omitted params that interleaves Tier-1 and Tier-2
  fills contiguously from one source. Example — `Iterable.joinToString`: `limit: Int = -1` is Tier 1;
  `separator`/`prefix`/`postfix`/`truncated` (`CharSequence = "…"`) and `transform (…)? = null` are Tier 2, so
  `list.joinToString("-") { … }` fills the omitted CharSequence defaults by positional splice (kcc) — keeping the
  trailing `transform` lambda in its own slot — or requires them (C#).

**Known edge (single-eval):** the call-site receiver-rewrite duplicates the receiver EXPRESSION into the spliced default,
so a receiver with side effects that is read by a `= this` default is evaluated more than once (a data-class `copy` or
`substringAfter` on a plain variable/literal — the common case — is unaffected). The remaining unhandled case is a
SAME-MODULE default that references another VALUE parameter (`b: Int = a * 10`): it still needs the callee's own scope and
is rejected at the omitting call (a real `$default` synthetic would lift it — a documented follow-up).

**#146 known gaps (named, not silent):** a non-const default that references a PRIVATE/internal library symbol
(`= privateHelper()`) is NOT poison-detected at stamp time — it is carried, then fails LOUDLY (imprecise) at the
consumer's re-lower (the private symbol is absent from the public ref surface → an unresolved `callStatic`/`FindStatic`),
not with a precise stamp-time diagnostic; a stamp-time IR-walk detection is a cheap later add. And a GENERIC injected
top-level function's non-const default still loud-refuses (the generic call path keeps `filledArgExprs`, which has no
`defaultArg` placeholder). Both are authoring-time refusals, never a miscompile.

## 8. Reverse / cross-assembly interop

- A DotKt assembly is a first-class .NET assembly; C# can reflection-load it. For **compile-time** `<Reference>`/
  `<ProjectReference>`, the emitted BCL `TypeRef`s (all scoped to the single `System.Private.CoreLib` that
  Reflection.Emit produces) are repointed to the real contract assemblies (`Object`/`Task`→`System.Runtime`,
  `List`/`Dictionary`→`System.Collections`, …) by the build-time `retarget` (Mono.Cecil). See memory
  `r1-reverse-projectreference-retargeter`.
- Forward (`Kotlin → .NET`): `import System.X` / a `<ProjectReference>` to a C# project just works (the import scan
  injects the referenced types into FIR). See `docs/design-kotlin-metadata-attributes.md` and memory
  `c2-import-driven-resolution`, `s5-fir-injection-seam`.

## 8b. Dual representation: `import System.Text.StringBuilder` vs `kotlin.text.StringBuilder` — two typed VIEWS of one CLR type

A BCL type that the stdlib **also** aliases (`kotlin.text.StringBuilder` is `@ClrTypeAlias`-bound to
`System.Text.StringBuilder`; same pattern for the aliased collections, exceptions, …) can appear in a program under
**two distinct frontend identities**:

- **the stdlib view** — default-imported, Kotlin-flavored members (`append`, `length`, the stdlib extensions);
- **the imported .NET view** — `import System.Text.StringBuilder` injects the RAW reflected surface
  (`Append`, `Length`, `ToString`, every BCL overload) as a separate classifier at its .NET namespace.

**The rule (deliberate, decided 2026-07-02): both views are legal and coexist; they are NOT unified.** Each is a
typed view of the SAME CLR runtime type (both erase to `System.Text.StringBuilder` at emit), but the frontend keeps
them distinct:

- Used separately in one program, both just work (`il-dualrep` gates this).
- **Mixing identities is a frontend type error** with an explicit message
  (`actual type is 'System.Text.StringBuilder', but 'kotlin.text.StringBuilder' was expected`). This is the
  intended diagnostic, not a bug.
- **Escape hatch**: an explicit cast crosses the views — `net as kotlin.text.StringBuilder`
  (plus `@Suppress("CAST_NEVER_SUCCEEDS")`; the frontend can't know they unify, the runtime checkcast is a no-op
  because the CLR type is literally the same).

Why not unify (the Kotlin/JVM `typealias` precedent)? Resolving the import TO the stdlib type would erase the very
thing the import asks for — the raw .NET member surface — and breaking the .NET view would break real interop code
(`sb.Append(...)`, `sb.Length`). Rejecting the import needs the alias map (`@ClrTypeAlias`, stdlib ref.dll) in the
frontend path, which the layer rules forbid (kotc reads no CLR-binding metadata). Two views with a clear boundary
diagnostic is the clean 1.0 rule; an explicit `clrView<T>()`-style conversion intrinsic is possible later if the
cast proves too blunt.

## 8c. Injected .NET STATIC members: implicit `Type.member` works (`.Companion` optional)

A facadegen-injected .NET class's static members surface on a synthesized **companion object**, and resolve
**implicitly** — exactly like a hand-written Kotlin companion:

```kotlin
import Avalonia.Application
Application.Start(...)             // implicit companion access — the natural form
Application.Companion.Start(...)   // explicit form — still works, identical BIR
```

The old rule requiring `.Companion` (2026-06-23; MEMORY `injected-static-members-need-companion`) was **retired
2026-07-03**: it was a wiring gap, not a pinned-compiler limitation. Stock K2 only links `companionObjectSymbol`
(the field the implicit-qualifier path consults) for source/deserialized classes — never for a fully-generated
owner — so kotc now eagerly creates + links the companion itself and sets the FIR-internal `ownerGenerator`
attribute through a bytecode-public Java shim (`kotc/frontend/FirInternals.java`; the eager link makes the
framework's only assignment site unreachable, upstream `FirGeneratedScopes.kt:245-255`/`:290`). Instance members,
constructors, properties, events, operators and extension methods resolve directly as before.

## 8d. Same-name .NET arity families: `Task` stays `Task`, `Task<TResult>` becomes `Task1<TResult>`

.NET allows a non-generic type and generic definitions that differ **only by arity** in one namespace
(`Task`/`Task``1`, `TaskCompletionSource`/`TaskCompletionSource``1`, `Tuple`/`Tuple``1..``8`, `Func``1..``17`,
`IComparable`/`IComparable``1`). C# disambiguates at the use site by type-argument count; a **Kotlin classifier
cannot be overloaded by arity** (one classifier per `(package, name)` — a K2 hard limit, typealiases included).
DotKt therefore projects the names (the `kotlin.Function0/Function1/…` precedent):

- the family's **non-generic** member keeps the plain simple name: `Task`, `Tuple` — so `Task.Delay(100)`,
  `Task.WhenAll(...)` read exactly like C#;
- each **generic** definition in a multi-member family is named `<Simple><arity>`: `Task1<TResult>`,
  `TaskCompletionSource1<T>`, `Func2<T,R>`, `Tuple2<A,B>`;
- a **singleton** generic family keeps the plain name: `List<T>`, `IEnumerable<T>` (`IEnumerable` non-generic
  lives in a *different namespace*, so no clash), `HashSet<T>`, …

The family is computed against the **loaded reference universe** (all `--refs` assemblies + BCL), not the
emitted closure, so a type's Kotlin name never changes when an unrelated import is added. `import
System.Threading.Tasks.Task` injects the **whole family** (both `Task` and `Task1`); `import
System.Threading.Tasks.Task1` also works (facadegen maps the trailing digits back to the CLR backtick arity).
In the injection metadata the .NET-name token is the **true CLR name** (`System.Threading.Tasks.Task``1`), so
the wire format itself is collision-free.

**Implementing an arity-family generic interface uses the arity-qualified name + the VERBATIM .NET member surface.**
`class Ver : IComparable1<Ver> { override fun CompareTo(other: Ver?): Int }` — the classifier is `IComparable1`
(the generic member of the `System.IComparable` family), and its member is the .NET name `CompareTo` with an
NRT-oblivious `Ver?` parameter, NOT the Kotlin operator `compareTo`. (facadegen surfaces .NET members verbatim — it
does not camelCase or operator-map them; the `compareTo`/operator restoration applies only to **round-tripped DotKt**
assemblies, whose members are already Kotlin-named.) The natural spelling `IComparable<Ver>` does **not** resolve to
the generic — `IComparable` is the non-generic (arity-0) member, so it errors *"no type arguments expected"*; this is
the arity hard-limit above, not a bug. **For Kotlin comparability, implement `kotlin.Comparable<T>` instead** — it
gives the `operator compareTo` / `<` and emits BOTH CLR faces (`System.IComparable``1<T>` and the non-generic
`System.IComparable` cast-and-forward bridge), so a BCL consumer's natural-ordering dispatch works.

**Nested generic types are not injected** (`List<T>.Enumerator` — no CLR-addressable open name in the meta
grammar); members referencing them degrade to `Any?`. Iteration is unaffected (`for (x in list)` rides the
injected `IEnumerable<T>` iterator marker, and the backend enumerates via `GetEnumerator/MoveNext/Current`).

## 8d. .NET event subscriptions and closeable tokens

A .NET **event** (`ObservableCollection.CollectionChanged`, a WinForms/WPF `Button.Click`, a custom library
`Widget.Changed`) is consumed through a closeable subscription token:

```kotlin
val c = ObservableCollection<Int>()
c.CollectionChanged.subscribe { sender, e -> println("scoped") }.use {
    c.Add(1)                                                  // automatically unsubscribed at the end of use
}
```

- The event surfaces as a **read-only property** `CollectionChanged: ClrEvent<HandlerFn>`, where `HandlerFn` is
  the handler's **Kotlin function type** (`(Any?, Any?) -> Unit`) — so a lambda `{ s, e -> … }` binds directly.
  `ClrEvent<T>` (`kotlin.clr.ClrEvent`) is a **compile-time-only handle**: a .NET event is not a first-class value
  (you can only add/remove/raise it), so `c.CollectionChanged` never materializes an object — it exists only to make
  `subscribe` resolve. The compiler binds it to the event's underlying **add/remove accessors**; the handler lambda
  is wrapped as the event's own delegate type (not `Action`/`Func`).
- `subscribe(handler)` adds the handler and returns `kotlin.clr.EventSubscription<HandlerFn>`, an
  `AutoCloseable`. `close()` is idempotent and removes the **same handler instance** that was added, so a direct
  lambda can be safely scoped with `use` without separately retaining it.
- Public `+=` / `-=` are intentionally not exposed: they split handler identity and subscription lifetime across
  caller-managed values. This also replaces the earlier `add_<Event>` / `remove_<Event>` accessor-method spelling.
- **Static events** subscribe the same way. A **static** event on a normal class is reached through the companion
  (`TaskScheduler.UnobservedTaskException.subscribe(h)`); a static event on a `static class`/`object`
  (`System.Console.CancelKeyPress.subscribe(h)`) is a member of that object. Either binds to the event's **static** add/remove
  accessor (a plain `Call`). (facadegen originally emitted only *instance* events of *non-static classes*.)
- **Interface events** (`INotifyPropertyChanged.PropertyChanged`) surface as a `ClrEvent<T>` member and **consume**
  the same way on an interface-typed receiver (`n.PropertyChanged.subscribe(h)`). When a Kotlin class **subclasses** a .NET
  class that already implements the interface (`class MyApp : Avalonia.Application`), the inherited concrete
  `ClrEvent<T>` fake-override satisfies the slot and is elided — nothing to declare.
- **Implementing and raising a CLR event from Kotlin** (MVVM / `INotifyPropertyChanged`): a Kotlin class that directly
  implements an event-bearing interface writes `override val E by clrEvent()` to synthesize the event (a backing
  delegate field + real `add_E`/`remove_E`/`raise_E` accessors); a `ClrEvent<T>` handle exposes `invoke(sender, args)`
  to **raise** it. **Deliberate CLR-native deviation (interop-first):** C# permits raising an event only from **within**
  its declaring type, but the DotKt `ClrEvent<T>` handle exposes raise, so `vm.PropertyChanged.invoke(vm, args)` is
  legal from **outside** the declaring type (it calls a public synthesized `raise_` accessor — the `.event`'s `.fire`).
  This is exactly the raiser .NET libraries hand-roll as `protected virtual void OnPropertyChanged(...)`, promoted to a
  first-class part of the event handle — it is what lets the idiomatic property-delegate MVVM pattern
  (`ViewModelProperty.setValue` raising a base class's `PropertyChanged`) work. Consistent (a handle uniformly supports
  `subscribe`/`invoke`), documented (here + `docs/design-clr-event-model.md`), convincingly explainable — passes all
  three conditions of the acceptance test. A **consumed** foreign .NET event has no synthesized `raise_`, so `invoke`
  on it stays an error — you still cannot raise an event you did not declare. Full model:
  [`docs/design-clr-event-model.md`](design-clr-event-model.md).

## 8e. A .NET delegate parameter surfaces as a Kotlin FUNCTION TYPE — even when its Invoke takes/returns `object`

A .NET method/ctor parameter typed as a delegate is injected as a Kotlin **function type** (`(A) -> R`), so a lambda
binds directly and — when it is a `virtual` — a Kotlin subclass can **override** it naturally. This holds **even when
the delegate's `Invoke` has an `object`/`Any?` param or return** (#1): `SendOrPostCallback.Invoke(object)` surfaces as
`(Any?) -> Unit`, so `class MyCtx : SynchronizationContext() { override fun Post(cb: (Any?) -> Unit, state: Any?) }`
resolves. (Previously such a delegate collapsed to a bare `Any?`, and the override matched *nothing*.)

- **Overload on delegate-typed params — a bare lambda binds the preferred sibling (#19).** When a .NET type overloads
  a member on two delegates that differ only at their function positions — by adjacent **arity**
  (`Thread(ThreadStart)` = `() -> Unit` **and** `Thread(ParameterizedThreadStart)` = `(Any?) -> Unit`) or by a
  Unit-vs-value **return** (`Task.Run(Action)` = `() -> Unit` **and** `Task.Run(Func<T>)` = `() -> T`) — a bare no-arrow
  `{ … }` lambda would be an *overload resolution ambiguity* (its arity/return is unspecified, matching both). facadegen
  — the only layer that sees the whole overload group — marks the **Pareto-dominated** sibling (the wider-arity /
  value-returning one) `lowPriority`, and kotc stamps `@kotlin.internal.LowPriorityInOverloadResolution` on the
  synthesized declaration. So a bare `Thread({ … })` binds `ThreadStart` and `Task.Run({ … })` binds `Action` with **no
  ambiguity**, while an explicit `Thread({ x -> … })` (or a method reference) still reaches the wider
  `ParameterizedThreadStart` — it is then the sole applicable candidate. Preference order (lower = preferred): fewer
  function params first (arity 0 before 1), then a Unit-returning delegate before a value-returning one; two
  equally-preferred delegates tie and neither is deprioritized. Coverage:
  `tests/interop/consumer/fixtures/ThreadingInteropTests.kt` and `DelegateOverloadTests.kt`.

## 8f. A SOURCE declaration wins over a facadegen-injected copy of the same identity (#15)

If the SAME top-level type or function is BOTH declared in the compiled Kotlin **source** AND injected by facadegen
from a referenced .NET/DotKt assembly, the **source declaration wins** and the injected copy is suppressed. This
arises from a project **mislayout** — the app's `**/*.kt` glob reaches a `<ProjectReference>`'d library's *own source
files*, so the app compiles `class Plain` / `fun hello()` from source while facadegen *also* injects `demo.Plain` /
`demo.hello` from the referenced dll. Before #15 the injected copy collided with the source (`overload resolution
ambiguity` at the use site + `conflicting overloads/declarations` at the source decl site) — only when the name was
actually used. Now the source declaration is authoritative and emits as a plain local type/call.

- **Granularity is package + name, not signature.** Suppression is per (package, name) and per *kind* (a source `val
  hello` does not suppress an injected `fun hello`, and vice versa), but a source top-level function of a **different
  signature** (`fun hello(s: String)`) still suppresses the referenced dll's `fun hello(): Int` overload — so that
  referenced overload becomes unreferenceable (a **loud** unresolved-reference, never a silent miswire). The fix for
  this is to not compile the referenced library's source into the app (correct the glob / project layout); the
  source-wins rule only guarantees the compiler no longer *doubles* the declaration.
- **MPP residual.** The shadow query sees only the current module's source, so a **common-module** source declaration
  does not suppress a **platform-session** injection of the same identity.

## 9. Reference-type nullability ⇔ .NET NRT; un-annotated .NET types are PLATFORM types

A Kotlin value-type `X?` is the structural `System.Nullable<X>` (§ value types). The **not-null assertion**
`v!!` on such a value (`Int?`/`Long?`/`Double?`/`Byte?`…) is therefore not a no-op: kotc lowers `CHECK_NOT_NULL`
to a `Nullable<X>.HasValue` test that throws `NullPointerException` on empty and otherwise unwraps the bare
value via `Nullable<X>.Value` (the same `nullableHasValue`/`nullableValue` nodes the smart-cast path uses, #15).
A raw pass-through would leave the `Nullable<X>` **struct** on the stack where the use site expects the bare
value — invalid IL for `v!! + 1`, garbage for `v!!.toLong()`, and no throw for `null!!` (#56). A
**reference-type** `X?` has no
structural form on the CLR (a reference is always null-capable), so it rides **.NET's own nullable-reference metadata**:
ilemit stamps `[NullableContext(1)]` per type (reference positions default to non-null) and `[Nullable(2)]` on each
nullable reference return/parameter — the exact encoding the C# compiler uses, so a **C# consumer also sees** DotKt's
`String?` as nullable. There is no DotKt-specific nullability attribute.

Reading the other direction, consuming **any** .NET assembly:

| the .NET reference type's NRT info | injected Kotlin type |
|---|---|
| `[Nullable(2)]` / nullable context | `T?` |
| `[Nullable(1)]` / non-null context | `T` |
| **none** (assembly never opted into NRT) | `T!` — a **platform type** |

`T!` is a flexible type `(T..T?)` (`ConeFlexibleType`): the consumer may use it as `T` or `T?` and the compiler
enforces neither — exactly how Kotlin/JVM treats un-annotated Java. This avoids the unsound alternative of forcing a
possibly-null .NET value into a Kotlin non-null type.

### 9a. Platform-type `T!` null-legitimacy — a null flows to the dereference (no eager boundary assertion)

The flexibility of `T!` is settled between the frontend and `bir2cir`: `facadegen` emits a `TypeNode.Oblivious` for an
NRT-oblivious .NET member (`NrtByteOf == 0`, `Program.cs:ApplyNrt`), and `kotc`'s `ClrTypeInjection.coneOf` maps it to a
`ConeFlexibleType(T, T?)` in an OUTPUT position (a getter/return) — where it stays flexible so a `[MaybeNull] T` keeps
its platform-type null-checkability — while an INPUT/param `T!` type-variable collapses to the bare `T` (#157). Fir2Ir
attaches the `@kotlin.internal.ir.FlexibleNullability` marker onto the flexible IR type (kotc installs the
`JvmIrSpecialAnnotationSymbolProvider` — see `ClrCliPipeline`), so `kotc`'s `BirEmitterTypes.birType` **emits
`TypeNode.Oblivious`** for it rather than collapsing it to a plain `nullable`. `bir2cir` then **lowers `Oblivious` to the
bare inner** (a value `Int!` → bare `int32`; a reference `String!` → a bare NRT-oblivious ref) — never a `Nullable<T>`
wrapper — so **`ilemit` never sees a platform type** (it has no oblivious case). This settles the null-legitimacy
question:

- **No spurious null-check is inserted when a `T!` is used as a non-null `T`.** kotc emits nothing at the implicit
  boundary — a `T!` value assigned to a `T` local, passed to a `T` parameter, or returned as a declared `T` is a plain
  copy of the reference. Inserting a check here would *violate* platform-type laxity (the developer has taken
  responsibility), so its absence is correct, not a gap.
- **A genuinely-null `T!` used LOCALLY flows UNCHECKED until a real dereference**, where the CLR raises
  **`NullReferenceException`** — the CLR analogue of the JVM's NPE. The throw lands at the **use site** (the member
  access / call on the null receiver), not at the assignment/return boundary that first admitted the null. kotc inserts
  nothing at this CALLER-side implicit boundary — a `T!` assigned to a `T` local or returned as a declared `T` is a
  plain copy of the reference; a check here would violate platform-type laxity (the developer took responsibility).
- **But if the null is PASSED INTO a public/protected function's non-null parameter, it fails fast at that CALLEE
  entry** with a Kotlin-messaged `NullPointerException` — the public-surface parameter precondition (§9c). So the
  CALLER-side boundary stays lax (this bullet), while the CALLEE's own declared non-null contract is enforced. This is
  the one place the JVM's eager `checkNotNullParameter` assertion IS reproduced (deliberately, as a contract).
- **`?.` and smart-casts behave normally.** A safe-call `t?.member` on a `T!` receiver null-gates in the frontend
  (returns `null`, never throws / never asserts). A flow smart-cast (`if (t != null) t.member`) works: the frontend
  proves non-null-ness by control flow and picks the lower bound, needing no runtime assertion.

Practical upshot for interop: treat a `T!` from an un-annotated .NET assembly as you would an un-annotated Java value —
if you use it as non-null LOCALLY and it is actually null, you get a `NullReferenceException` at the point you
dereference it; if you pass it to a public Kotlin non-null parameter, you get a Kotlin `NullPointerException` at that
call's entry (§9c). Prefer `T?` + a null check (or `?:`) at the boundary when the .NET API can legitimately return null.

### 9a-bis. A VALUE-type platform `T!` has NO null state — it is the CLR default (`0`), and `== null` is statically false (#8)

§9a is written for REFERENCE platform types (a reference is always null-capable in IL). A VALUE-type platform member —
a facadegen-injected `[MaybeNull]`/un-annotated .NET member whose type is a value type, e.g. `ThreadLocal<Int>.Value` —
behaves differently, because a CLR value type has no null representation:

- The oblivious `Int!` lowers to a **bare `int32`**, NOT `System.Nullable<Int32>`. Reading `ThreadLocal<Int>().Value`
  when the slot was never set yields the CLR **default `0`**, whereas the SAME code on Kotlin/JVM (where `ThreadLocal`'s
  `T` is a boxed `Integer` and `.get()` is `@Nullable`) yields `null`. This is a deliberate divergence: DotKt reifies
  the type argument to the value type `int32`, so the .NET runtime's own default applies. (Contrast the reference twin
  `ThreadLocal<String>().Value`, which IS `null` when unset — a reference platform type keeps a real null.)
- Because the value has no null state, a `threadLocalInt.Value == null` comparison is **statically false**, and a
  `threadLocalInt.Value ?: fallback` elvis always yields the value (the fallback is dead). No `Nullable<T>.HasValue`
  test or `.Value` unwrap is emitted — there is no wrapper struct to unwrap.

This falls out of the reification rule (§2, `clr-all-type-args-reified`): a platform `T` reified to a value type takes
that value type's null-lessness. If you need a genuine "unset vs 0" distinction over a value type, use an explicit
Kotlin `Int?` (which IS `Nullable<Int32>`), not the platform default.

**Writing** a bare value works (`threadLocalInt.Value = 5`). A value-type platform slot has no null state, so a source
that carries one is coerced at the `clrPropSet` boundary (`ValueSlotNullableWrite`, bir2cir — the WRITE twin of the #8
read side), NOT stored as a `Nullable<Int32>` (the setter is a bare `int32`):

- A `Nullable<V>` source — a genuine Kotlin `Int?` (`threadLocalInt.Value = someIntQ`) — is **unwrapped** to the bare
  `V` the slot expects (bir2cir emits the `nullableValue` = `Nullable<V>.get_Value()`). If the source is dynamically
  `null` at runtime, `get_Value()` throws `InvalidOperationException` — the faithful "there is no value to store into a
  null-less slot" outcome (contrast Kotlin/JVM, where the boxed slot would just hold `null`).
- A **literal `null`** source (`threadLocalInt.Value = null`, compile-legal under platform laxity) is a **loud bir2cir
  emit-time error** — a CLR value type has no null representation, so a silent `default(V)` (0) would mask a user bug.
  Use an explicit Kotlin `Int?`-typed property/variable when you need nullable value storage.

Only a **bare** value slot triggers this: a genuine `Nullable<V>` .NET property (a real `int?` slot) or a
`ThreadLocal<Int?>` (a `Nullable<Int32>` slot) keeps the source verbatim, and a reference slot (`ThreadLocal<String>`)
is untouched — a `String!` platform reference has a real null.

### 9c. Non-null CONTRACTS on the public surface — fail-fast at the callee boundary (#6)

Independent of platform-type laxity (§9a is a CALLER-side fact), DotKt synthesizes JVM-Kotlin-style design-by-contract
checks on the CALLEE side of the PUBLIC/PROTECTED surface, reproducing (and slightly extending)
`Intrinsics.checkNotNullParameter`. These are ordinary BIR emitted by kotc (visibility + nullability are frontend
facts); kotc names no CLR type — `kotlin.NullPointerException` resolves to the BCL exception via the same
`@ClrTypeAlias` path a user `throw NullPointerException(...)` uses.

- **Parameter preconditions.** Every PUBLIC or PROTECTED member (top-level fun, member fun, constructor, property
  setter, default interface method, and `@PublishedApi internal`) checks each NON-NULL REFERENCE **value parameter** at
  entry: `if (p == null) throw NullPointerException("Parameter specified as non-null is null: <owner>.<method>,
  parameter <p>")`. A null crossing a boundary (a platform `T!`, an unsound cast, reflection ignoring NRT) into such a
  param fails fast at the entry with a Kotlin-messaged NPE instead of propagating to a later, mis-sited `NullReferenceException`.
- **Return postconditions (a DotKt addition beyond JVM Kotlin).** A NON-NULL REFERENCE return of a public/protected
  member (top-level/member fun, property getter, default interface method) is checked at each `return`: the value is
  bound to a temp, yielded when non-null, else `throw NullPointerException("<owner>.<method>, non-null return value is
  null")`. This guards a null leaking OUT via a platform type or an unsound generic — the callee promised non-null.
  SUSPEND functions are excluded (kotc emits their body plainly; bir2cir's Continuation state-machine rewrites the
  returns, and wrapping would collide with that shape).

Scope discriminator, and the deliberate deviations from JVM Kotlin (both directions share the same discriminator):
- **Value types are skipped** — a primitive/unsigned (`Int`/`UInt`/…) or a Kotlin `value`/inline class is a CLR value
  type, never null; a null-check would be ill-typed. Nullable `T?`, `Unit`, and `Nothing` are skipped too.
- **Type-parameter-typed params are skipped ENTIRELY.** On the CLR generics are REIFIED, so a bare `T` may instantiate
  to a value type; a null-check would force a box and be meaningless. Stricter than JVM (which erases `<T : Any>` to
  `Object` and DOES check it) — a `clr-all-type-args-reified` deviation.
- **Receivers are NOT checked** (dispatch or extension), only value parameters. A dispatch receiver can never be null;
  an extension receiver can be LEGITIMATELY null on the CLR — a Kotlin extension on a companion object
  (`fun String.Companion.format(...)`) lowers to a static method whose receiver is a null singleton (companion-object
  elision), so asserting it fires spuriously. JVM asserts the extension receiver; DotKt does not.
- **private/internal/local/inline members are skipped** — trusted within the module; and an `inline` body travels as a
  splice payload, so a check would apply inconsistently (cross-module vs same-module splices).
- **Constructor preconditions land AFTER the base/`this` ctor delegation** (the delegation rides a separate BIR field),
  not before `super()` as on the JVM — so a null user param first dereferenced by a base-ctor argument NREs before the
  friendly NPE. An accepted ordering deviation.

## 9b. `System.Byte` is UNSIGNED — it maps to `kotlin.UByte`, not `kotlin.Byte` (STRICT, #53)

Kotlin's `Byte` is **signed** (−128..127); the CLR's `System.Byte` is **unsigned** (0..255). So the mapping is strict
in both directions, exactly parallel to the wider unsigned widths (`UInt16↔UShort`, `UInt32↔UInt`, `UInt64↔ULong`):

| Kotlin | .NET | why |
|---|---|---|
| `kotlin.Byte` (signed) | `System.SByte` | both signed 8-bit |
| `kotlin.UByte` (unsigned) | `System.Byte` | both unsigned 8-bit |
| `kotlin.ByteArray` | `System.SByte[]` | signed native array |
| `kotlin.UByteArray` | `System.Byte[]` | **unsigned native array** |

Consequences:
- A .NET `byte` member (return/param/field) surfaces to Kotlin as **`UByte`**, and a `byte[]` as **`UByteArray`** (the
  specialized primitive array — a native `System.Byte[]`, **not** `Array<UByte>` and **not** a value-class wrapper). A
  .NET byte value `200` reads as `UByte 200`, where the previous lossy collapse to signed `Byte` gave `-56`. This makes
  `UByte`/`UByteArray` round-trip faithfully through a DotKt emit → re-consume cycle.
- `UByteArray` is represented at runtime as a native `System.Byte[]` (like `ByteArray` is `System.SByte[]`); its
  `ubyteArrayOf`/indexing/`size`/iteration are native array operations, not calls on a wrapper object.
- **Escape hatch for signed-byte consumers:** `UByteArray.toByteArray()` and `ByteArray.toUByteArray()` reinterpret
  between the two. On the CLR `System.Byte[]` and `System.SByte[]` share identical storage and are freely
  interchangeable at runtime (ECMA reduced-type array compatibility), so these lower to a **reinterpret cast — a VIEW,
  not a defensive copy** (a deliberate `discard-jvmisms` deviation: the JVM's `copyOf()` is defensively copying the same
  8-bit storage; on the CLR the two arrays are already the same bytes). Mutations through one view are visible through
  the other. The scalar `UByte.toByte()` / `Byte.toUByte()` remain bit-reinterprets of a single 8-bit value as before.

## 10. Round-trip fidelity audit — what re-consuming a DotKt assembly as Kotlin LOSES

§6 lists what survives the round-trip (Kotlin → DotKt `.dll` → re-consumed as Kotlin: `facadegen` reflects the dll and
reads the `[Kotlin*]`/NRT attributes, the FIR injector rebuilds the declarations). **This section is the inverse: the
Kotlin surface that the round-trip does NOT fully restore.** It is an *audit* (prioritized-task #8) — the gaps are
documented here, not yet fixed. Findings are grounded in `toolchain/facadegen/Program.cs` (the reconstructor),
`toolchain/ilemit/Emitter.Metadata.cs` + `Emitter.CompilerServices.cs` (the attribute stampers), and
`toolchain/kotc/.../BirEmitter.kt` (the emitter), and were cross-checked with Codex against the CLR-metadata surface.

Three buckets — **Restored** (faithful), **Partial** (degraded), **Lost** (no carrier).

### 10.1 Restored (faithful) — see §6

`infix`/`operator`/`suspend`, top-level functions, cross-module `inline` non-local-return, `val`-vs-`var`,
reference-type nullability, parameter names (named-arg calls), constant default args, `vararg`, extension receivers,
reified generics, and `final`/`open`/`abstract` + `public`/`protected` visibility. **Data-class generated members
also round-trip**: `componentN()` carries `operator` (via `[KotlinFunction(Operator)]`, set from `fn.isOperator` in
`BirEmitter.kt`), so destructuring works cross-module, and `copy`/`equals`/`hashCode`/`toString` are real callable
methods. **Generic constraints/bounds and declaration-site variance also round-trip** (gap ①, now fixed): `facadegen`
reads `GetGenericParameterConstraints()`/`GenericParameterAttributes` and emits `tvariance`/`tbound`/`mbound` metadata,
and `ClrTypeInjection` restores `out`/`in` (interfaces) + upper bounds — so `interface P<out T>`, `interface C<in T>`,
`class SortedPair<T : Comparable<T>>`, and `fun <T : Comparable<T>> …` keep their variance and bounds cross-module.
**`sealed` classes/interfaces also round-trip (gap ⑤, now fixed):** a Kotlin `sealed` type lowers to a CLR
abstract-class / interface (which drops the sealed modality), so `ilemit` stamps `[KotlinSealed]`, `facadegen`
emits a `sealed` meta line, and `ClrTypeInjection` restores `Modality.SEALED`. Cross-module this restores the full
sealed contract: the modality, **cross-module inheritance enforcement** (a rogue subclass in another module is
rejected), **and exhaustive `when`** with no `else` — the closed inheritor set is rediscovered because the sealed
type's subtypes are themselves injected into the consumer's session via their `super` edges (importing
`Circle`/`Square` alongside `Shape` makes `when (s) { is Circle -> …; is Square -> … }` exhaustive).
**Function types with a receiver (`A.() -> B`, #145) and suspend function types (H2) also round-trip
faithfully, not as a plain delegate:** the CLR signature slot itself either keeps the delegate with the
receiver riding as its first type argument (receiver types — `bir2cir` does NOT erase it) or erases fully
to `object` (suspend types — a suspend-lambda value is a `Continuation`-based state machine, not a
`Func`), so in both cases `bir2cir`'s `RoundtripMetadata` stamps a carrier — a bare
`[KotlinExtensionFunctionType]` marker, or a `[KotlinSuspendFunctionType(shape)]` pre-erasure TypeNode —
across every param/return/field/property position. `facadegen` reads both back (`SuspendFnNode` /
`HasExtFnMarker`+`WithExtRecv`) and `kotc`'s `ClrTypeInjection` reconstructs the real cone type: a genuine
`kotlin.coroutines.SuspendFunctionN` (so a passed lambda re-binds as an actual suspend lambda), or a
`FunctionN` carrying the `ExtensionFunctionType` cone attribute (so the lambda gets a real implicit
`this: P` and its body's unqualified members resolve against the receiver) — not an ordinary `Func<>`
that has lost the distinction.

### 10.2 Partially restored (in metadata, but degraded on reconstruction)

| Kotlin construct | What survives | What degrades / is lost |
|---|---|---|
| **Generic constraints / bounds** (`<T : Comparable<T>>`, `where`) | **NOW RESTORED (gap ①, §10.1)** | ~~`facadegen` never read `GetGenericParameterConstraints()`~~ — it now does, emitting `tbound`/`mbound` metadata that `ClrTypeInjection` restores as upper bounds (a `Comparable<T>` bound is reversed from the CLR `System.IComparable<T>` it lowers to). Multiple bounds (a `where` list) round-trip as several lines. |
| **Declaration-site variance** (`class Box<out T>`, `interface Cmp<in T>`) | **NOW RESTORED for interfaces (gap ①, §10.1)** | `facadegen` now reads `GenericParameterAttributes` and emits `tvariance`, which `ClrTypeInjection` restores as `out`/`in`. **Class**-type-param variance still has no CLR form (stays invariant); **use-site** variance / **star projection** `Foo<*>`: no analog, lost. |
| **`fun interface` (SAM)** | a plain interface | **The `fun interface` NATURE now round-trips (gap ③):** `ilemit` stamps `[KotlinFunInterface]`, `facadegen` emits a `funinterface` meta line, and `ClrTypeInjection` restores `status.isFun`. So a consumer sees it as a functional interface and can implement it (incl. via an anonymous `object : Handler { … }`). **What still degrades:** a bare **lambda** (SAM conversion) does NOT convert — blocked by the pinned Kotlin **2.4.0** FIR `FirSamResolver.computeSamCandidateNames`, which scans `FirRegularClass.declarations` **directly** for the single abstract method's name; a `FirDeclarationGenerationExtension`-injected interface serves its members lazily via scopes (empty `declarations`), so no SAM candidate is found. Same class of pinned-compiler limitation as `object`/companion (§10.4 #2) — not fixable from our side without materialising the SAM method into `declarations` (against the plugin contract) or a compiler bump. |
| **`enum class`** | entry values | A *basic* enum → a real CLR `enum` → `facadegen` restores it as an **`object` of `val`s** (value access like `Color.GREEN` works); a *rich* enum (ctor args / methods / per-entry bodies, `isRichEnum`) → a singleton-field **class** → restored as a plain **`class`**. Either way it is **not** a Kotlin `enum class`: exhaustive `when`, `.entries`/`values()`/`valueOf`, `.ordinal`/`.name` identity degrade. **Not fixable via the injection path (gap ④):** a `FirDeclarationGenerationExtension` (2.4.0) cannot synthesize real `FirEnumEntry` declarations — the exhaustiveness checker (`FirWhenExhaustivenessTransformer`) enumerates `enumClass.declarations.filterIsInstance<FirEnumEntry>()`, and the plugin API exposes no `createEnumEntry`/entry hook (only `createTopLevelClass`/`createMemberProperty`/…). Generating `ClassKind.ENUM_CLASS` with enum-shaped `val`s would mislead FIR without giving exhaustiveness, so no `[KotlinEnum]` carrier is emitted. |
| **`data class`** | generated members (10.1) | The **`data` modifier itself** is not carried (consumer sees an ordinary class). A `copy(field = x)` with the generated **self-referential defaults** (`y = this.y`) **now works** — same-module and cross-module — via the positional receiver-rewrite fill (§7). |
| **Annotations** | RUNTIME/BINARY-retained with CLR-legal args; `KClass`→`System.Type` | `ilemit` **skips** annotations whose ctor-arg shape the CLR encoder rejects (`BuildCab`/`TryCab` → diagnostic, e.g. a generic-instantiation parameter). **SOURCE**-retention annotations are gone. **Use-site targets** (`@get:`/`@field:`/`@param:`) are only as faithful as which CLR target they landed on — the Kotlin intent is ambiguous. Repeatable-annotation semantics differ. |
| **Default arguments** | constants + receiver-referencing non-constant defaults (§7) | A non-constant default that references the RECEIVER (`= this`) round-trips (positional splice / receiver-rewrite). Only a default that reads another VALUE parameter (`b = a * 10`) is still rejected at the omitting call (needs a `$default` synthetic). |
| **`internal` visibility** | hidden cross-assembly (correct for module≈assembly) | `kotc` lowers `internal`→ CLR `assembly`; `facadegen.Vis` skips assembly-visible members, so they don't inject — aligned with Kotlin's module boundary, but the **`internal` modifier is not itself restorable**, there is **no friend-module / `InternalsVisibleTo`** wiring, and no JVM-style name mangling. |

### 10.3 Lost (no carrier — not reconstructable from the current metadata)

| Kotlin construct | Closest .NET shape | What is lost |
|---|---|---|
| **`object` singleton** | class + static `INSTANCE` field | Restored as a plain **`class`**; the Kotlin singleton access `MyObject.member` does **not** round-trip (a consumer would need `.INSTANCE`/`.Companion`). |
| **Companion implicit access** | synthesized companion (`sfun`/`sprop`) | ~~must be written `Class.Companion.member`~~ **LIFTED (2026-07-02, `50c2c9f`)**: implicit `Class.member` now resolves — kotc eagerly creates+links the injected class's companion and sets the FIR-internal `ownerGenerator` via a bytecode-public Java shim (`kotc/frontend/FirInternals.java`; the old NPE was a FIR wiring gap, not a K2 limit). Both forms compile to identical BIR. |
| **`value`/inline class** (`@JvmInline`) | a **real wrapper class** (never erased, never a struct) | The OPPOSITE of Kotlin/JVM: no inline-class erasure and no name mangling — `Money` is emitted as an ordinary reference class (backing field + property + the synthesized `equals`/`hashCode`/`toString`), i.e. permanently "boxed". Structural equality survives; what is lost is the value-ness itself (identityless-ness is not enforced, no .NET `struct`, and the `value` modifier does not round-trip). The frontend still REQUIRES `@JvmInline` (JVM-frontend checker); the emitted `[kotlin.jvm.JvmInline]` attribute is skipped by ilemit. |
| **`typealias`** | the expanded type | The alias name is not visible cross-module (it is expanded at use). |
| **Contracts** (`@ExperimentalContracts`) | — | `callsInPlace`/returns-implies smart-cast facts are gone → consumer loses the smart-casts. |
| **`Nothing`** (bottom type) | erased to `object` | The bottom-type semantics (unreachable, `List<Nothing>` covariance) have no CLR analog. **The RETURN position now round-trips (#133, FIXED):** a `fun f(): Nothing` return carries a `[KotlinNothing]` marker — `bir2cir` records the pre-erasure fact (`BirTypeLowering`, alongside the `object` erasure) and stamps the marker (`RoundtripMetadata`), `facadegen` reads it (`RetTypeSfxN`) and surfaces `kotlin.Nothing`, and `kotc`'s `coneOf` resolves the bare `Nothing` node to `bt.nothingType`. So a consumer's `val y: String = if (c) "ok" else f()` keeps `String` instead of widening to `Any?` (`roundtrip-nothing-return`). Nested occurrences (`List<Nothing>`, parameter positions, a `suspend fun`'s Task-wrapped result) stay lost. |
| **`lateinit`** | a non-null `var` field | The definite-init contract / `isInitialized` is lost (restored as a plain non-null `var`). |
| **`inner` class** | a nested type | The `inner` modifier (implicit outer `this` capture) is not marked vs. a plain nested class. |
| **`const val`, `tailrec`, `crossinline`/`noinline`, property delegation `by`** | literal field / plain method / accessors | Compile-time-only facts: the value/behavior survives but the modifier/relationship is not a restorable declaration fact. (Mostly harmless — these don't change the callable API surface.) |

### 10.4 Highest-impact gaps (for a follow-up fix pass)

1. ~~**Generic constraints + interface variance dropped by `facadegen`**~~ — **FIXED (gap ①, 2026-07-01).** `facadegen`
   now reads `GetGenericParameterConstraints()` / `GenericParameterAttributes` and emits `tvariance`/`tbound`/`mbound`
   metadata; `ClrTypeInjection` restores `out`/`in` variance + upper bounds (lazy lookup-tag cones, self-ref-safe for the
   BCL numeric tower, fail-soft). Covers every generic library API (`<T : Comparable<T>>`, `Comparator<in T>`, …). No new
   attribute — reconstructor-side only (`facadegen` emission + injector consumption).
2. **`object` singleton round-trip** — the **companion IMPLICIT access** (`Type.member`) is **FIXED (2026-07-02,
   `50c2c9f`):** kotc eagerly creates + links the injected class's companion (setting the FIR-internal
   `ownerGenerator` via the `FirInternals.java` shim — the old NPE was a FIR wiring gap, not a pinned-K2 limit), so
   implicit `Class.member` resolves for both facadegen-injected .NET statics (§8c) and round-tripped Kotlin
   companions (§10.3). MEMORY `injected-static-members-need-companion` is RESOLVED. **The residual (accepted):** a
   re-consumed top-level **`object` singleton** restores as a plain **class**, so its members are reached through the
   class / `.INSTANCE`, not the Kotlin singleton sugar `MyObject.member`.
3. **`fun interface` SAM** — **PARTIALLY FIXED (gap ③, 2026-07-02).** The `fun interface` *nature* now round-trips
   (`[KotlinFunInterface]` → `funinterface` meta → `status.isFun`), so a consumer sees a functional interface and can
   implement it (anonymous `object`). A bare **lambda** still won't SAM-convert — pinned-2.4.0 FIR `computeSamCandidateNames`
   reads `FirRegularClass.declarations` directly, which a generation-extension interface leaves empty (§10.2). **KNOWN /
   ACCEPTED LIMITATION** on the same basis as #2. (**re-verified on 2.4.0 — task #114: still limited.** Empirically
   re-run under the 2.4.0 pin: (a) a bare **lambda** where an injected `fun interface` is expected still does NOT
   SAM-convert — `argument type mismatch: … but 'Handler' was expected` / `cannot infer type for value parameter` — while
   the fun-interface *nature* still round-trips (`funInterface:true`) so an anonymous `object : Handler {…}` works; (b) a
   re-consumed **`enum class`** still restores as an `object` of `val`s (value access `Color.GREEN` compiles, but an
   exhaustive `when` over it fails `'when' expression must be exhaustive`) — see #4; (c) a re-consumed top-level **`object`
   singleton** still restores as a plain **`class`** with a static `INSTANCE` field — `Config.member` is `unresolved`,
   `Config.INSTANCE.member` compiles — see #2. All three are confirmed, not pending.)
4. **`enum class`** — **NOT FIXED (gap ④).** Blocked at the injection layer: a `FirDeclarationGenerationExtension` (2.4.0)
   cannot synthesize real `FirEnumEntry` declarations, which FIR's exhaustiveness checker requires; the plugin API has no
   enum-entry hook (§10.2). A basic enum still round-trips as an `object` of `val`s (value access works). **KNOWN /
   ACCEPTED LIMITATION.**
5. **`sealed` hierarchies** — **FIXED (gap ⑤, 2026-07-02).** `[KotlinSealed]` → `sealed` meta → `Modality.SEALED`
   restores the modality, cross-module inheritance enforcement, AND exhaustive `when` (the injected subtypes supply the
   closed inheritor set). See §10.1.
6. **Non-constant default args / `data class copy` self-defaults** — **MOSTLY FIXED (kcc review C3, 2026-07-06).** The
   omitted middle default no longer shifts a later provided arg's slot: kotc fills positionally (a `{"k":"defaultArg"}`
   placeholder for a @KotlinDefault-carrying cross-module callee, spliced by `bir2cir.DefaultArgSplice`; a same-module
   default inlined directly). A RECEIVER-referencing default (`missingDelimiterValue = this`, a `copy`'s `y = this.y`)
   round-trips via the `this`→call-receiver rewrite. **Residual:** a default that reads another VALUE parameter
   (`b = a * 10`) still needs the callee scope and is rejected at the omitting call (a real `$default` synthetic would
   lift it); and the receiver-rewrite is single-eval only for a trivial receiver (§7).

7. **Generic-fidelity gaps surfaced by the atomicfu CLR port (#133)** — **ALL THREE FIXED.** Three
   DOWNSTREAM-of-facadegen gaps (the facadegen symbol surface was verified correct in each; the
   `roundtrip-generic-inline-ext` / `roundtrip-generic-operator` / `roundtrip-nothing-return` sections now PASS).
   (a) A **generic inline extension on a generic receiver** (`inline fun <T> Cell<T>.update(fn:(T)->T)`) — `kotc`'s
   facadegen inline-**splice** path (`BirEmitterInline.inlineSpliceCall`) now threads the extension receiver in
   `recvs.extension` (the same shape the owner-less splice uses; owner stays the facadegen file class so `bir2cir`'s
   owner-ful `ResolveInlinePayload` finds the `[KotlinInline]` body); route: **kotc**. (b) A Kotlin **`operator get`/`set`
   on a generic DotKt type** — `bir2cir`'s `NetInteropBinding` now keeps the plain emitted `get`/`set` method (which the
   Kotlin type declares) instead of the BCL `get_Item`/`set_Item` fallback when the owner has no .NET indexer property;
   route: **bir2cir**. (c) The **`Nothing` return** carrier (§10.3) — `bir2cir` records + stamps `[KotlinNothing]` and
   `kotc` `coneOf` resolves it to `bt.nothingType`; facadegen's reader was already landed. Route: **bir2cir + kotc**.

Status: **#1 (variance/bounds), #5 (sealed), #6 (default args), #7 (atomicfu generic-fidelity gaps) are FIXED; #2 companion IMPLICIT access is FIXED
(`50c2c9f`); #3 (fun interface) is PARTIAL** (nature restored, SAM-lambda pinned-compiler-blocked). **#4 (enum
class) remains a KNOWN / ACCEPTED limitation** — blocked by the pinned Kotlin 2.4.0 `FirDeclarationGenerationExtension`
surface (no `FirEnumEntry` synthesis), not by a missing `[Kotlin*]` attribute we could add. The only object/companion
residual is the **`object` singleton `.INSTANCE`** round-trip (#2), not implicit companion access.

---

## Quick "this surprised me" index

- `inline`/`reified` written but no lambda passed → **ignored** (plain/generic method). §2, §3.
- `reified` lets you pass a non-reified type param on the CLR (JVM forbids it). §2.
- `tailrec` **is** tail-call optimized — kotc rewrites a self-tail-call into a back-jump loop, so deep tail recursion runs in constant stack like the JVM. §2b.
- Inlining is done by the backend at emit, not the frontend. §3.
- A non-local `return` into a cross-module inline lambda → works (body is carried in `[KotlinInline]`). §3.
- `println(true)` prints `True`, `println(4.0)` prints `4`. §5.
- `String.toInt()/toLong()/toByte()/toShort()` are strict base-10 (like JVM): no leading/trailing whitespace, no group
  separators, and a bad string throws `NumberFormatException` (a `catch (IllegalArgumentException)` also catches it). §5.
- `String.toDouble()/toFloat()` parse with **InvariantCulture** (a `,` is always rejected, never a group separator, so
  `"3,14".toDouble()` throws like JVM). The accepted grammar is otherwise .NET's `Double/Single.Parse`
  (decimal point, sign, exponent, `NaN`/`Infinity`); the JVM-only hex-float and trailing `d`/`f` suffix forms are **not**
  accepted. Failure throws `NumberFormatException`. §5.
- `String.format` uses the .NET composite format (`"{0}"`), not Java printf (`"%d"`). §5.
- `suspend fun` has no Continuation parameter — it returns `Task<T>`, and it starts **hot** (like C# `async`), not cold. §4.
- A `CharSequence` parameter surfaces to C# as `string`; a `StringBuilder` passed as `CharSequence` is **snapshotted** by an implicit `.toString()` — no live view. §5b.
- A Kotlin `Map` surfaces to C# as a *mutable* `IDictionary<K,V>`; `keys`/`values`/`entries` are snapshots. §5c.
- A `value class` is a real (reference) class on the CLR — never erased, never a struct. §5f.
- An auto-property's backing field is named `<Name>k__BackingField` (C# convention, `[CompilerGenerated]`), not `Name` — so reflection never sees a property and a field under one name. §5h.
- `System.Byte` is UNSIGNED → maps to `UByte` (and `byte[]` → `UByteArray`, a native `System.Byte[]`); `kotlin.Byte` is signed = `System.SByte`. `UByteArray.toByteArray()` is a reinterpret VIEW, not a copy. §9b.
- `import System.Text.StringBuilder` and `kotlin.text.StringBuilder` are two distinct typed views of one CLR type; mixing them is a type error (cast to cross). §8b.
- An injected .NET class's statics resolve implicitly (`Application.Start(...)`); `.Companion` is optional. §8c.
- Two same-simple-named classes in different packages coexist (packages are namespaces now). §1.
- A reference type from a .NET assembly built WITHOUT `<Nullable>enable</Nullable>` arrives as a platform type `String!`, not `String`. §9.
- A null platform-type `String!` used as non-null does **not** throw at the boundary — no assertion is inserted; the null flows to the first dereference, where the CLR throws `NullReferenceException` (faithful to Kotlin, = JVM with call/param assertions off). §9a.
- Re-consuming a DotKt `.dll` as Kotlin now **restores** generic **bounds/interface variance** (gap ①), **`sealed`** (gap ⑤ — modality, cross-module enforcement, exhaustive `when`), the **`fun interface` nature** (gap ③ — usable, though a bare lambda still won't SAM-convert under the pinned 2.4.0 compiler), and **receiver function types / suspend function types** (`A.() -> B`, #145; `suspend (…) -> T`, H2 — a passed lambda gets a real implicit receiver / re-binds as a genuine suspend lambda, not a plain `Func<>`); a re-consumed **companion resolves implicitly** (`50c2c9f`); `enum class` and top-level **`object` singletons** still restore as a plain `class` (the `.INSTANCE` singleton sugar is lost). §10.
