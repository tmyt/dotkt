// The BASIC `enum class` DECLARATION lives in a SEPARATE file from its use site (EnumTests.kt) — a
// same-assembly cross-file #90 repro. bir2cir must collect basic-enum names MODULE-WIDE (across every
// .bir.json), not per-file, or `EnumBasic.A.toString()` in EnumTests.kt would not see EnumBasic's
// `kind:"enum"` and would dead-end in ilemit. (Migrated from cases/il-enumtostr/enum.kt.)
enum class EnumBasic { A, B, C }
