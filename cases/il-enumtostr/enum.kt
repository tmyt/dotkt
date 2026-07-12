// The `enum class` DECLARATION lives in a SEPARATE file from its use sites (app.kt) — a same-assembly
// cross-file #90 repro. bir2cir must collect basic-enum names module-wide (across every .bir.json), not
// per-file, or `E.A.toString()` in app.kt would not see E's `kind:"enum"` and would dead-end in ilemit.
enum class E { A, B, C }
