# Using .NET from Kotlin

DotKt is a **pure binding**: it ships no wrapper library. You call real .NET types directly, and
the surface you see in Kotlin is the type's real reflected surface.

## 1. `import` a .NET type — that's the whole mechanism

Any type from any referenced .NET assembly (the BCL, a NuGet package, a C# `<ProjectReference>`)
becomes visible by importing its .NET name. No façade generation step, no binding file:

```kotlin
import System.Text.StringBuilder

fun main() {
    val sb = StringBuilder()
    sb.Append("hello").Append(", ").Append("dotnet")
    println(sb.ToString())   // hello, dotnet
    println(sb.Length)       // 13
}
```

- Members keep their **.NET names** (`Append`, `Length`, `ToString`) and all overloads.
- Constructors, instance methods, properties, indexers, and chaining all work as in C#.
- **Import aliases** work: `import System.Text.StringBuilder as SB`.

Every public reference declaration is projected. Types appearing in signatures, including return types,
parameters, and supertypes, therefore resolve without import-seeded discovery:

```kotlin
import TX.Panel
import TX.Widget

fun main() {
    val panel = Panel()
    val w = Widget("w1")
    panel.Children.Add(w)                // IList<Widget> — real constructed generic
    println(panel.Children[0].Name)
    println(w.Make().Core().Size)        // Gadget and Sprocket were never imported
}
```

## 2. Static members — just like Kotlin

A .NET class's statics surface on a synthesized companion object, and you call them the natural
Kotlin way — `Type.member` works directly (the explicit `.Companion` form is also accepted):

```kotlin
import Kfc.App

fun main() {
    App.start { p -> println("p=$p") }   // static method (lambda → .NET delegate)
    println(App.Count)                   // static property
    println(App.Answer)                  // static field
    App.Companion.start { }              // the explicit form works too (same BIR)
}
```

## 3. Lambdas, delegates, and events

A Kotlin lambda converts to the required .NET delegate type — including BCL `Func<>`/`Action`
**and custom generic delegates**:

```kotlin
println(p.Apply({ x -> x + 5 }, 10))     // Func<int,int>
println(GenDel().Run({ v -> v * 3 }, 2)) // delegate T Mapper<T>(T)
```

.NET **events** are exposed as `add_<Event>` / `remove_<Event>`:

```kotlin
import System.Collections.ObjectModel.ObservableCollection

val c = ObservableCollection<Int>()
c.add_CollectionChanged { sender, e -> println("changed") }

val h: (Any?, Any?) -> Unit = { _, _ -> println("h fired") }
c.add_CollectionChanged(h)      // subscribe a stored handler…
c.remove_CollectionChanged(h)   // …so it can be unsubscribed (delegate equality)
```

## 4. `out` / `ref` parameters — `byref(...)`

`kotlin.clr.byref` marks an argument as a .NET `out`/`ref` parameter (both unify to one form):

```kotlin
import P.Calc
import kotlin.clr.byref

var q = -1
val ok = c.TryDivide(10, 2, byref(q))   // out int → writes q = 5
c.Swap(byref(x), byref(y))              // ref int, ref int → swapped in place
```

A `ref`-returning method received plainly gives you a value copy; bind it with
`var x by byref(m())` to keep a **live** reference whose writes flow back into the .NET storage.

## 5. Nullable value types, .NET enums, operators, extension methods

- `int?` / `double?` ⇔ Kotlin `Int?` / `Double?` in both directions (passing a plain `Int` or
  `null` into an `int?` parameter both work).
- A .NET **enum** imports as an object of enum-typed values: read, pass, `==`, and `when` over it
  (a `when` needs an `else` — .NET enums are open).
- C# **operator overloads** (`op_Addition`, …) surface as Kotlin operators: `a + b`, `-a`, etc.
  (`==` deliberately routes to `Equals`, the Kotlin semantics.)
- C#-defined **extension methods** surface as Kotlin extension functions.

## 6. One CLR type, two views — imported type vs. stdlib alias

Some BCL types are *also* what a Kotlin stdlib type is bound to (`kotlin.text.StringBuilder` IS
`System.Text.StringBuilder`; Kotlin's `List`/`Map` ARE the BCL collection interfaces). If you
`import System.Text.StringBuilder`, you get the **raw .NET view** (`Append`, `Length`) as a
*separate* Kotlin type from the stdlib view (`append`, `length`):

- Use either view; both work and both are the same CLR type at runtime.
- **Mixing them in one expression is a type error** — the intended diagnostic, not a bug.
- Escape hatch: an explicit cast crosses views (`net as kotlin.text.StringBuilder`); the runtime
  cast is free because the CLR type is literally the same.

## 7. Consuming your Kotlin from C#

A DotKt assembly is plain public IL. A C# project can `<ProjectReference>` a `.ktproj`; Kotlin
classes, properties, `suspend` functions (as `Task<T>`), and nullability annotations (`String?`
→ NRT) all surface naturally. Top-level functions live on the `<FileName>Kt` static class.

For the full mapping rules and deliberate deviations, see
[Kotlin on the CLR — what's different](kotlin-on-clr-differences.md) and the canonical
[`docs/dotkt-semantics.md`](../dotkt-semantics.md).
