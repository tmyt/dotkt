/*
 * Copyright 2010-2023 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// The CLR-free half of the kotlin.clr coroutine surface, declared as `expect` HERE (jar-INCLUDED common)
// so `import kotlin.clr.blockOn` / `import kotlin.clr.delay` resolve at the FRONTEND from the classpath
// with ZERO kotc special-casing (docs/design-coroutine-cold-core-task-bridge.md §12 "P4 symbol-surfacing
// mechanism", user-directed 2026-07-03). Their SIGNATURES name no CLR type — only the ACTUAL bodies touch
// Task — so they split cleanly into an expect (here) + two actuals across two SEPARATE K2 compilations:
//   - the frontend jar (build-stdlib-jar.sh) stages a throwing STUB actual (never executed — the jar is a
//     pure frontend classpath); EXACT precedent = the @OptionalExpectation JvmName/JvmInline stub actuals.
//   - build-stdlib-{ref,rt}.sh compile the REAL actual in clr/taskinterop/kotlin/clr/Coroutines.kt.
// `await` is NOT here: its signature names Task, so it is surfaced by facadegen, not expect/actual (§5).

package kotlin.clr

/**
 * Runs [block] as a coroutine on the cold core and BLOCKS the calling thread until it completes —
 * the `runBlocking` analog for CLR entry points / tests. Rethrows the block's exception raw.
 */
public expect fun <T> blockOn(block: suspend () -> T): T

/** Suspends for [ms] milliseconds via `Task.Delay` (a value beyond Int.MAX_VALUE delays forever). */
public expect suspend fun delay(ms: Long)
