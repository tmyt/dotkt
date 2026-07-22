// Migrated il batch M3 — the SIBLING FILE half of il-inlsiblingdelegate (was cases/il-inlsiblingdelegate/b.kt).
// Kept in its OWN fixture file (no @TestAttribute here) so the `newDelegate` its inline body materializes is
// lifted into THIS file's file class — a DIFFERENT file class than M3SdWrap's use site in InlineMaterializationTests.kt.
// That preserves the original F4 (#63) subject: when M3SdWrap splices m3Sd_bPick inside a materialized carrier,
// the deposited newDelegate's provenance is a SIBLING file, so `_appLocalMethods` must be MODULE-WIDE (else the
// sibling target is mis-judged non-app-local -> HasUnmaterializableNested fail-loud). All top-level names are
// M3-prefixed (one project = one namespace).
class M3SiblingBHolder    // present only so this file has a named top-level decl; unused

fun m3Sd_bSink(n: Int, g: (Int) -> Int): Int = g(n)

// An inline fn whose else-branch forwards a CAPTURE-LESS `{ it + 100 }` to the non-inline m3Sd_bSink — kotc lifts
// that lambda to a `__lambdaN` in THIS file's file class + a `newDelegate`.
inline fun m3Sd_bPick(cond: Boolean, primary: () -> Int): Int =
    if (cond) primary() else m3Sd_bSink(7) { it + 100 }
