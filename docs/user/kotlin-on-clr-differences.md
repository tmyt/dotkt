# Kotlin on the CLR — what's different from Kotlin/JVM

DotKt runs real Kotlin, but it is a **.NET program**, not a JVM program wearing a mask. Where
Kotlin's surface carries JVM-specific baggage, DotKt follows the .NET host instead — openly and
by design. This page is the friendly tour; the canonical, exhaustive reference is
[`docs/dotkt-semantics.md`](../dotkt-semantics.md).

## Printing follows .NET conventions

```kotlin
println(true)   // True   (not "true")
println(4.0)    // 4      (not "4.0")
```

Same program logic, host-native formatting.

## `String.format` uses the .NET composite format

```kotlin
String.format("{0} items, id {1:D5}", n, id)   // .NET "{0}" style
"{0,-8}|".format(name)                          // padding/alignment too
```

Not Java `printf` (`"%d"`). It binds straight to `System.String.Format`.

## `CharSequence` is `string`

`kotlin.CharSequence` has no faithful .NET equivalent, so DotKt models it as `System.String`:

- A `fun f(cs: CharSequence)` surfaces to C# as `void f(string cs)` — clean interop.
- A `StringBuilder` (or any non-`String` char sequence) passed into a `CharSequence` slot is
  snapshotted by an implicit `.toString()`. **It is a snapshot, not a live view** — mutating the
  builder afterwards is not observed through the parameter.
- You can still write `class S : CharSequence` — a user implementation keeps working via a
  synthetic interface.

## `suspend` functions are async `Task<T>` functions

```kotlin
suspend fun fetch(): Int          // CLR signature: Task<int> fetch()
```

- Calling a suspend function **is an await**; C# can `await` your Kotlin directly and vice versa.
- Execution is **hot, like C# `async`** — a suspend function starts running when invoked, not
  lazily on collection like kotlinx's cold-by-default builders.
- There is no `Continuation` parameter in any public signature.
- Status: the ABI is settled and locked in the design docs; the full coroutine/`Sequence` runtime
  is the current implementation track — check the release notes before relying on advanced
  builders.

## Generics are real (reified) — and that mostly helps

The CLR keeps type arguments at runtime, so `is T` / `T::class` use a real generic type. CLR type
arguments do not retain Kotlin nullability, so a reified function also receives a compiler-hidden
nullability witness: `x is T` correctly accepts null when called as `f<String?>`, including when the test
is inside a captured lambda, SAM, suspend lambda, or object expression. You can still pass
a non-reified type parameter into a reified function (legal here, an error on the JVM). `inline` is
otherwise a no-op unless you pass a lambda literal (then it's really inlined, including non-local
`return`, even cross-module).

## Collections are the BCL's

Kotlin `List`/`MutableList`/`Map`/... are bound to the real BCL collection types — a Kotlin
`List<Int>` is directly usable from C#. Two consequences:

- A Kotlin `Map` surfaces to C# as `IDictionary<K,V>` (mutable interface) — Kotlin's read-only-ness
  is enforced by the Kotlin compiler, not the CLR type. `Map.get` keeps Kotlin's
  null-on-missing semantics.
- `map.keys` / `values` / `entries` are **snapshots**, not the JVM's live views.

## `value class` is a real class

No inline-class erasure, no mangled names: `value class Money(val amount: Int)` emits
an ordinary class with structural equality. Simpler interop; the identityless-ness is not
CLR-enforced. The JVM-specific `@JvmInline` annotation is not required.

## Enums have two shapes

A **basic** enum (constants only) becomes a **real CLR enum** — usable in C# `switch`. A **rich**
enum (constructor params, methods, per-entry bodies) becomes a class with one singleton per entry
(`name`/`ordinal`/`values()` still work from Kotlin).

## Default arguments: constants travel, expressions mostly do too

A primitive/String/null constant default is native .NET metadata — **C# callers get it for
free**. Non-constant defaults (and String defaults on interface-typed params) are carried as
Kotlin metadata: Kotlin callers can omit the argument; C# callers must pass it explicitly.

## Public CLR classes can hide implementation interfaces

C# permits a public class to implement an `internal` interface and to satisfy a public interface member through a
private explicit implementation. Kotlin consumers see only the accessible public supertypes, but inherited members
remain concrete: subclassing such a .NET class does not create an “unimplemented abstract member” obligation. For an
explicitly implemented event, subscribing through the projected class member reaches the original C# add/remove
accessors and backing delegate; DotKt does not create a second event store.

## Context parameters are ordinary leading parameters on the CLR

`context(s: Scale) fun scaled(a: Int)` emits `scaled(Scale, int)` — a context parameter is a plain
positional parameter, placed **after** an extension receiver's `__self` and **before** the regular
parameters. That is the whole physical story: a C# caller passes it like any other argument, and a
Kotlin consumer of the emitted dll keeps writing `with(scale) { scaled(5) }` because the slot carries a
`[KotlinContextParameter]` marker that dll2klib restores as a real context parameter in the reference KLIB.

Context parameters need no compiler flag at language version 2.4 — they are on by default, and passing
`-Xcontext-parameters` produces `warning: the argument '-Xcontext-parameters' is redundant for the
current language version 2.4.` Functions, properties, members, extensions,
`suspend`, `inline`, defaults that read a context parameter, and context function types
(`context(A) (B) -> C`) all work.

## Not supported (current, honest list)

- **kotlinx libraries** (kotlinx-coroutines, kotlinx-serialization, …) — DotKt binds the
  *stdlib*; kotlinx is a separate future track.
- **Full coroutine machinery** (`Sequence` builders / `yield`, structured concurrency) — in
  progress; the `suspend`⇔`Task` ABI above is the settled design.
- **Live `CharSequence` views** and user implementations of `Appendable` (use `StringBuilder`).
- Consuming a DotKt dll **back as Kotlin** loses some declaration facts: `enum class`-ness,
  `object` singleton sugar, implicit companion access, SAM conversion of a bare lambda — each a
  pinned-Kotlin-2.4.10 limitation, documented in
  [`dotkt-semantics.md` §10](../dotkt-semantics.md).
- `internal` is assembly-visibility; there is no friend-module (`InternalsVisibleTo`) wiring yet.
