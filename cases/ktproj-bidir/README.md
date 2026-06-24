# ktproj-bidir — bidirectional ProjectReference (C# ⇄ Kotlin)

A Visual Studio–style solution where a C# project and a Kotlin `.ktproj` reference each
other through `<ProjectReference>` — in **both** directions, in one build graph:

```
cslib.csproj  ◄── klib.ktproj  ◄── app.csproj
  (C# lib)         (Kotlin lib)       (C# exe)
```

- **Forward** `klib.ktproj → cslib.csproj`: the Kotlin code does `import Theme.Palette`
  and uses the C# type directly (`Palette().Accent`). This is the long-working direction
  (import-scan → FIR injection from `@(ReferencePath)`).
- **Reverse** `app.csproj → klib.ktproj`: the C# host consumes the Kotlin class `Greeter`
  and its `List<String>` at **compile time** — full IntelliSense, no reflection. This is
  what R-1 / `retarget` unlocks: the emitted Kotlin assembly is retargeted so its
  BCL refs point at the real contract assemblies a C# compiler references, instead of the
  single `System.Private.CoreLib` `ilemit` emits (which causes `CS0012` otherwise).

A real cycle (A→B and B→A between the same two projects) is rejected by MSBuild; the point
is that `ProjectReference` now works in *either* direction, so a solution composes freely.

## Run

```sh
dotnet run --project app/app.csproj
```

Expected:

```
Hi, Visual Studio (accent=cyan)
Visual Studio A, Visual Studio B, Visual Studio C
```

`accent=cyan` proves the forward leg (Kotlin used the C# `Palette`); the two lines being
printed by a plain C# `Main` that `new`s a Kotlin `Greeter` proves the reverse leg.
