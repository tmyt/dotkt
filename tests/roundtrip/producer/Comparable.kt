// Migrated verify-roundtrip.sh section `roundtrip-comparable` (#179, end-to-end run) — the library half.
// A Kotlin `class C : Comparable<C>` lowers `compareTo` to the CLR `System.IComparable<C>.CompareTo` PascalCase
// slot (bir2cir DeclarationRename) and its supertype to `System.IComparable<C>`. facadegen renames the self-slot
// `CompareTo` -> `compareTo` + forces the operator flag (so `<`/`>`/`<=`/`>=` resolve to C's own operator) and
// restores the supertype as `kotlin.Comparable<C>` (so `sorted()`'s constraint is satisfied); bir2cir
// NetInteropBinding rebinds compareTo->CompareTo on the facadegen-injected owner so the calls run cross-module.
// (The facadegen-SURFACE assertion `roundtrip-comparable-meta` inspects the generated metadata JSON directly and
//  stays in tests/roundtrip/scenarios/run.sh — it has no in-process analog.)
package roundtrip.cmp

class Ver(val n: Int) : Comparable<Ver> {
    override fun compareTo(other: Ver): Int = n - other.n
}
