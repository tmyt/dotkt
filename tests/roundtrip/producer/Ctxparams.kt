// Cross-module CONTEXT-PARAMETER library half. A context parameter is an ordinary positional value parameter of
// the emitted CLR method (after the `__self` extension receiver, before the regular parameters), so a consuming
// module resolves and calls these through the re-imported dll like any other declaration.
package roundtrip.ctxparams

class Scale(val factor: Int)

context(s: Scale)
fun scaled(a: Int): Int = a * s.factor

// A context parameter AND an omitted non-constant default that READS it: the @KotlinDefault carrier's parameter
// index counts the context slot, so the consumer's omission fills from the right position.
context(s: Scale)
fun tagged(a: Int, label: String = "f" + s.factor): String = "$a/$label"

// A TIER-1 CONSTANT default behind a context slot: the constant rides native `[Optional]`+`[DefaultParameterValue]`
// and the consumer fills it from the dll2klib metadata, whose per-parameter list is PHYSICAL — so the context slot
// shifts `k`'s ordinal, and a lookup that counted regular parameters only would read the wrong entry.
context(s: Scale)
fun labeled(a: Int, k: Int = 7): Int = a + k + s.factor

context(s: Scale)
fun String.deco(a: Int): String = this + ":" + (a * s.factor)

class Holder(val base: Int) {
    context(s: Scale)
    fun combine(a: Int): Int = base + a * s.factor

    context(s: Scale)
    val reading: Int get() = base * s.factor

    // A MEMBER whose default reads the context parameter, with a LATER required argument — the shape that pins the
    // cross-module member call to the positional sequence (an omitted slot must become a placeholder, not vanish).
    // `inline` is load-bearing: it is what makes the declaration carry `@KotlinDefault` (see carriesKotlinDefault),
    // so the consumer's frontend accepts the omission and the fault is a SILENT wrong value rather than a diagnostic.
    context(s: Scale)
    inline fun pick(a: Int = s.factor, b: Int): Int = a * 100 + b

    // The member-EXTENSION form of the same shape: `__self` + context + an omitted default + a later required arg.
    context(s: Scale)
    inline fun String.pickExt(a: Int = s.factor + length, b: Int): Int = a * 100 + b
}

// A context FUNCTION TYPE in a public signature, both forms: receiver-less and receiver-carrying.
// A GENERIC member with an omitted default and a LATER required argument. The generic member call path builds its
// own arg vector, so it needs the same positional filling as the non-generic one — without it the omitted slot was
// deleted and `3` slid into `a` while the required `b` was zero-filled.
class GenHolder {
    fun <T> pick(a: Int = 7, b: T): String = "$a/$b"
    fun <T> String.pickExt(a: Int = 7, b: T): String = "$this:$a/$b"
}

class Boxy(val v: Int)
// TWO context-function-type parameters with DIFFERENT arities, adjacent — only a `,` separates their source ranges.
// Each slot's arity is keyed by its own source position, so this is what catches one slot being handed another's:
// a wrong pick changes the restored TYPE and the consumer's lambdas bind the wrong contexts.
fun pairFns(p: context(Scale) () -> Int, q: context(Scale, Boxy) () -> Int): Int =
    with(Scale(2)) { with(Boxy(5)) { p() * 10 + q() } }

// The two positions that can genuinely share an offset. Source ranges are HALF-OPEN, so a declaration's end offset
// IS its neighbour's start offset when no character separates them — `}val` below is legal Kotlin — and a leading
// comment additionally moves the FIR start off the IR start. Both slots must still restore their OWN arity.
// leading comment, deliberately: it shifts the FIR start of `flushA` off its IR start
fun flushA(): context(Scale) () -> Int = { contextOf<Scale>().factor }val flushB: context(Scale, Boxy) () -> Int = { contextOf<Scale>().factor * 100 + contextOf<Boxy>().v }

// The RETURN position of a receiver-carrying context function type, which restores through the method's `retAttrs`
// channel and whose value is a lifted lambda — the shape that returned the CONTEXT's field instead of the receiver's.
fun makeCtxFn(): context(Scale) Boxy.() -> Int = { this.v * 10 + contextOf<Scale>().factor }
fun evaluatePlain(f: context(Boxy) (Int) -> Int): Int = with(Boxy(10)) { f(5) }
fun evaluateRecv(f: context(Boxy) Boxy.() -> Int): Int = with(Boxy(10)) { Boxy(3).f() }

context(s: Scale)
val gauge: Int get() = s.factor * 3

// A context parameter beside an EXTENSION receiver on a property: the accessor is `prop_get<bumped>(__self, s)`.
context(s: Scale)
val Int.bumped: Int get() = this + s.factor

// `!0` and `!1` are distinct MethodDef parameter identities even though the permissive call-side index treats owner
// type variables uniformly. Property/MethodSemantics linking is declaration-side and must retain them.
class OwnerTypeVariableProperties<A, B> {
    val A.ownerSlot: String get() = "first"
    val B.ownerSlot: String get() = "second"
}
