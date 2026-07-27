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
// and the consumer fills it from the facadegen metadata, whose per-parameter list is PHYSICAL — so the context slot
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
}

context(s: Scale)
val gauge: Int get() = s.factor * 3

// A context parameter beside an EXTENSION receiver on a property: the accessor is `get_bumped(__self, s)`.
context(s: Scale)
val Int.bumped: Int get() = this + s.factor
