// Migrated verify-roundtrip.sh section `roundtrip-inline-member` (F1 / #60) — the library half.
// `class C { inline fun pick(block) }` restored from a DotKt assembly (isInline=true, body==null, a DISPATCH
// receiver). kotc emits a member-aware `callInline` carrying `recvs.dispatch`; bir2cir's InlineSplice §4.3
// binds it (the payload's member-field reads rebind to the caller-provided receiver) and routes a non-local
// `return` inside the block to the CALLER — not the delegate (the pre-F1 silent miscompile). `matched()` also
// exercises the dispatch-receiver `this.c` field read in the spliced body.
package roundtrip.picker

class C(val a: Int, val b: Int, val c: Int) {
    inline fun pick(block: (Int) -> Boolean): Int {
        if (block(a)) return a      // dispatch-receiver `this.a` read inside a spliced inline member body
        if (block(b)) return b
        if (block(c)) return c
        return -1
    }
}
