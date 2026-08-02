// Migrated IL fixture — the SIBLING FILE half of il-inlsiblingdelegate (was cases/il-inlsiblingdelegate/b.kt).
// Kept in its OWN fixture file (no @TestAttribute here) so the `newDelegate` its inline body materializes is
// lifted into THIS file's file class — a DIFFERENT file class than InlineCrossFileWrap's use site in InlineMaterializationTests.kt.
// That preserves the original F4 (#63) subject: when InlineCrossFileWrap splices inlineCrossFilePick inside a materialized carrier,
// the deposited newDelegate's provenance is a SIBLING file, so `_appLocalMethods` must be MODULE-WIDE (else the
// sibling target is mis-judged non-app-local -> HasUnmaterializableNested fail-loud). All top-level names are
// InlineCrossFile-prefixed (one project = one namespace).
class InlineCrossFileSiblingHolder    // present only so this file has a named top-level decl; unused

fun inlineCrossFileSink(n: Int, g: (Int) -> Int): Int = g(n)

// An inline fn whose else-branch forwards a CAPTURE-LESS `{ it + 100 }` to the non-inline inlineCrossFileSink — kotc lifts
// that lambda to a `__lambdaN` in THIS file's file class + a `newDelegate`.
inline fun inlineCrossFilePick(cond: Boolean, primary: () -> Int): Int =
    if (cond) primary() else inlineCrossFileSink(7) { it + 100 }
