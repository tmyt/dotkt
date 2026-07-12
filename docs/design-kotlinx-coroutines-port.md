# kotlinx.coroutines on the CLR cold core (Track 2) — feasibility + phased design (#110)

> Status (2026-07-12): SCOPING (Fable, audited against real kotlinx 1.10.2 source vs the cold-core v1 limits).
> **Verdict: FEASIBLE with ZERO compiler changes.** It is a BUILD-and-ACTUALIZE task (the stdlib pattern), NOT a
> port/redesign. Honest cost: **4-8 weeks** (volumetric compile-grind + ~25 CLR actuals + concurrency debugging),
> not days. The one stdlib change (`intercepted()`) is library-side and was explicitly parked awaiting this.

## 1. Feasibility (audited, not asserted)
The fixed compiler surface = the shipped cold core (`bir2cir/SuspendColdLowering.cs` + `SuspendLambdaLowering.cs`,
the `ContinuationImpl`/`SuspendLambda` protocol, `IntrinsicsClr.kt` kickoffs). kotlinx compiles on top as library.
Against the cold core's 3 hard v1 limits, a scan of kotlinx common+concurrent (287 files):
- **Suspension inside catch/finally** (`SuspendColdLowering.cs:555`, the one structural refusal): **0 sites** —
  kotlinx hoists suspension out of handlers (`catchImpl` returns the caught exception, suspends after).
- **Nested suspending try** (`:560`): 1 candidate (`catchImpl`) — false positive at the SM level (the inner try is
  inside the `collect{}` suspend lambda = its own SM).
- **Suspend member generic-on-own-AND-generic-class** (`:511-514`): **0 occurrences** (all use only class type params
  — the supported shape, gate cases il-coldgen/coldinst/coldvirt/coldabstract).
- All suspension entry points kotlinx uses are already lowered (`suspendCoroutineUninterceptedOrReturn`,
  suspend-value invoke, `createCoroutineUnintercepted(receiver, completion)`, cross-module inline+lambda via
  InlineSplice → `suspendCancellableCoroutine`, a public inline fun, splices into the caller and hits the F2 recognizer).
- Precedents: the real `SequenceBuilderIterator` compiles over the cold core (P5 gate-green); the harness `blockOn`
  is a mini-Track-2 (`runBlocking` over `startCoroutine`+Monitor).

**Genuine compiler blockers found: NONE.** Caveat: the audit is heuristic (name-based); the definitive check is the
build (untransformed `"suspend":true` decls hit the LOUD ilemit throw-stub, `SuspendColdLowering.cs:42-46` — nothing
fails silently). Any shape that surfaces is patched in the VENDORED kotlinx source (hoist suspension, kotlinx's own
idiom) as a documented vendor-diff — NOT a compiler change.

## 2. Source inventory (kotlinx 1.10.2)
| Source set | Files | Disposition |
|---|---|---|
| `common/src` | 259 | compile AS-IS |
| `concurrent/src` | 28 | compile AS-IS (real thread-safe LockFreeLinkedList, channels) |
| `jvm/src` | 329 | NOT compiled — its non-debug core (~850 lines: EventLoop/Builders/DefaultExecutor/Dispatchers/CoroutineContext) is the TEMPLATE for the CLR actuals; scheduling/debug/stream/future replaced or dropped |
| `jsAndWasmShared/src` | 20 | template for trivial/thread-agnostic actuals |
| `native/src` | 26 | template for multithreaded-simple actuals |

~55 `expect` declarations need CLR `actual`s (the driver work) — **every one implementable over facadegen-surfaced
BCL, no new interop machinery** (Monitor `@ClrIntrinsic`, Interlocked byref via `@ClrRefArgument`, `@Volatile`,
delegate adapters all exist + gate-covered: AtomicsClr.kt, il-atomics, il-monitordrain).

## 3. The one stdlib change (library-side) — make `intercepted()` real
The linchpin of Dispatchers. Today `ContinuationImpl.intercepted()` is identity (v1 stub, `ContinuationImpl.kt:132`).
`launch{}` → `startCoroutineCancellable` → `createCoroutineUnintercepted` → **`.intercepted()`** must return
`context[ContinuationInterceptor]?.interceptContinuation(this)` (= kotlinx's `DispatchedContinuation`) so every resume
goes through `dispatcher.dispatch`. With identity, every coroutine runs undispatched. The seam is fully plumbed
(`startCoroutine`, F2 `newSafeContinuation` already route through it) — only the body is a stub. Change (~15 lines, JVM
port): cache `intercepted = context[ContinuationInterceptor]?.interceptContinuation(this) ?: this` + a
`releaseIntercepted()` hook from `BaseContinuationImpl.resumeWith`'s completion path (for kotlinx's reusable
continuations). PLUS a 1-line `RootContinuation.resumeWith` fidelity clause: treat stdlib `CancellationException`
(extends IllegalStateException on CLR, `CancellationExceptionClr.kt:11`) like OCE → `trySetCanceled` (same clause as
#86 P0 — pure .NET fidelity, no dialect). Both are `libraries/stdlib/` edits.

## 4. CLR-actual (driver) designs
- **Dispatchers.Default** → `ThreadPool.UnsafeQueueUserWorkItem` (~20 lines; the .NET ThreadPool IS a work-stealing
  scheduler — do NOT port `scheduling/CoroutineScheduler`). **IO** → same pool behind `limitedParallelism`. **Main** →
  a `SynchronizationContext`-posting dispatcher (else `MissingMainCoroutineDispatcher`). **Unconfined** → pure common +
  `commonThreadLocal` actual → `System.Threading.ThreadLocal<T>`. Slots in via §3's `intercepted()` + the `_context`
  slot; the SM never knows about dispatchers.
- **Event loop / runBlocking / delay**: `EventLoopImplBase` is common over atomicfu; the platform actual needs thread
  identity + park/unpark. **Trap**: `LockSupport.unpark` is permit-based (unpark-before-park not lost); `Monitor.Pulse`
  is lost with no waiter → use a permit flag under a per-thread monitor / `SemaphoreSlim(0,1)` timed Wait (the harness
  BlockOnSink + il-monitordrain show the shape). `runBlocking` = port jvm Builders.kt over the parker. `DefaultExecutor`
  = a dedicated background `Thread` running the loop with `Monitor.Wait(obj, ms)`; `nanoTime` → `Stopwatch.GetTimestamp`
  (monotonic, NOT DateTime).
- **Structured cancellation (Job)**: **100% common library code** (JobSupport = an atomicfu state machine; cancellation
  = CancellationException through the same resumeWith chains; invokeOnCompletion = LockFreeLinkedList nodes). **No CTS
  in core semantics; nothing auto-inserted** — consistent with the #86 principle. CTS only as OPTIONAL interop adapters
  in a `kotlinx-coroutines-clr` companion (P7): `Job.asCancellationToken()`, `CancellationToken.asJob()`,
  `Deferred.asTask()`, `Task.awaitCancellable()` (OCE→CancellationException).
- **atomicfu**: the compiler plugin does NOT run (no IR plugin stage) → ship a CLR `kotlinx.atomicfu` RUNTIME library
  (~400-600 lines, "boxed mode": AtomicRef/Int/Long/Boolean/Array + loop/update + TraceBase no-op) over the stdlib's
  `kotlin.concurrent.atomics` (already Interlocked-backed via @ClrRefArgument, gate-covered). Memory-model risk LOW
  (Interlocked = full fences ≥ JVM assumptions). AtomicBoolean/Reference use Monitor-CAS today (correct, slower) — an
  Interlocked.CompareExchange-object binding is a perf follow-up, not a blocker.

## 5. Build integration
Mirror the stdlib triple: `build-kotlinx-{klib,ref,rt}.sh` alongside `build-stdlib-*.sh`; vendor 1.10.2 common+concurrent
under `libraries/kotlinx.coroutines/{common,concurrent,clr}` (clr = actuals + atomicfu); feed kotc `-Xmulti-platform
-Xexpect-actual-classes -Xcommon-sources` (lib.sh `stdlib_fragment_args`), stdlib klib on `-classpath`. **P0 infra spike**:
a second stdlib-dependent klib + two klibs on an app classpath. **Fallback that always works**: source-inclusion mode
(compile kotlinx into the app like `cases/support`) — prove semantics first, graduate to a separate assembly after
(cross-assembly suspend/inline round-trip already built: MemberBinding.Suspend, [KotlinInline] stash). Interop bonus:
every public suspend fun gets the `Task<T>` bridge → C# sees `channel.Receive(): Task<T>` natively.

## 6. Phasing (compiles as ONE unit — Job/select/channels/flow are API-entangled; phasing = runtime-validation rungs)
| Phase | Content | Effort | Risk |
|---|---|---|---|
| P0 vendor+spike | vendor 1.10.2; atomicfu CLR runtime; build scripts; klib-dep spike (fallback: source-inclusion) | 2-4d | Med |
| P1 cold-core enabler | real `intercepted()`+`releaseIntercepted`+RootContinuation clause; rung: hand-written interceptor dispatches to a pool thread (kotlinx-free) | 1-2d | Low-Med (regression-gated) |
| **P2 full-module compile** | ~290 files through the pipeline + ~25 CLR actuals; definitive untransformed-suspend audit | **1-3 WEEKS — THE unknown** | High (volumetric; each gap fixed library/actual-side) |
| P3 core runtime green | runBlocking+launch+delay; async/await; withContext; cancellation tree; CoroutineExceptionHandler; Unconfined+event loop | ~1w | Med |
| P4 real parallelism | Dispatchers.Default/IO over ThreadPool; newFixedThreadPoolContext; stress rungs | 3-5d | Med (concurrency heisenbugs; SM under contention) |
| P5 Flow | flow/collect/operators/SharedFlow/StateFlow (SafeCollector actual) | 3-5d | Low-Med |
| P6 Channel+select | BufferedChannel segment machinery under parallelism; select | ~1w | Med-High (most atomics-intense) |
| P7 interop+gates | kotlinx-coroutines-clr adapters; gate integration, cases, docs | 2-4d | Low |

**Total 4-8 weeks.** Cut-1 scope: Job/scope/launch/async/withContext/runBlocking/delay, Dispatchers, cancellation incl.
suspendCancellableCoroutine, Mutex/Semaphore, Flow, Channel, select. Out: debug probes, ThreadContextElement,
stream/future (→ P7 adapters), ticker/actor, stack-trace recovery (identity like js/native). The purge is NOT reversed
(kotc's isSuspendCancellable/runBlocking recognizers stay dead; NEW kotlinx klib/ref/rt built by this toolchain join
the gates; the JVM jar never returns).

## 7. Fable-vs-grind
**High-leverage (design/diagnosis):** the P1 `intercepted()`/`releaseIntercepted` protocol change; the
LockSupport→Monitor permit-parker (lost-wakeup trap); atomicfu memory-model + AtomicReference Interlocked upgrade; the
CancellationException↔OCE boundary (both directions); the klib-dep spike; **any SM miscompile that only reproduces under
P4/P6 parallelism** (cold-core bugs surfaced by kotlinx — the highest-value finds). **Mechanical Opus grind:** vendoring,
build scripts, the ~25 actual ports, the P2 compile whack-a-mole, rung/case authoring, XFAIL bookkeeping.

## Critical files
- `libraries/stdlib/clr/kotlin/coroutines/clr/internal/ContinuationImpl.kt` — the P1 `intercepted()`/`releaseIntercepted` (the ONLY cold-core edit)
- `libraries/stdlib/clr/taskinterop/kotlin/coroutines/clr/internal/RootContinuation.kt` — CancellationException → canceled-Task clause
- `toolchain/bir2cir/SuspendColdLowering.cs` — read-only reference (the fixed transform + v1 limits the port stays inside)
- `scripts/build-stdlib-klib.sh` + lib.sh — template for `build-kotlinx-{klib,ref,rt}.sh`
- `libraries/stdlib/clr/kotlin/concurrent/atomics/AtomicsClr.kt` + clr/builtins/Atomics.kt — substrate for the CLR kotlinx.atomicfu runtime
