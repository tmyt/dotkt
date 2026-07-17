// #87: a MEMBER `inline fun` with a NON-LOCAL-RETURN lambda, DECLARED on a superclass and called through a SUBCLASS
// receiver — both a plain subclass AND a self-referentially-bounded generic subclass (`Seg<S : Seg<S>>`, the
// kotlinx.coroutines `Segment<S : Segment<S>>` hierarchy). Such a call resolves to a FAKE OVERRIDE whose `parent` is
// the subclass and whose `body` is null, but the [KotlinInline] payload is stashed (bir2cir InlineBirStash) under the
// REAL DECLARING class. kotc must resolve the fake override so the `callInline` owner names the declaring class AND
// the same-module splice path fires (a fake override has a null body, which otherwise misroutes to the cross-module
// path) — else bir2cir InlineSplice reports `no [KotlinInline] payload found` (the `Segment.nextOrIfClosed` blocker).

private val CLOSED = Any()

internal open class Base {
	var slot: Any? = null
	// MEMBER inline fn with a non-local-return lambda: the closed marker fires the caller's `return`.
	inline fun firstOr(onClosed: () -> Nothing): Any? {
		val cur = slot
		return if (cur === CLOSED) onClosed() else cur
	}
	// Own-class self-call (receiver static type = Base): the non-inherited splice, keyed under Base directly.
	fun headOrNull(): Any? = firstOr { return null }
}

internal class Derived : Base()                       // plain subclass -> inherits firstOr as a fake override
internal abstract class Seg<S : Seg<S>> : Base()      // self-bounded generic subclass -> fake override too
internal class ConcreteSeg : Seg<ConcreteSeg>()

// Call sites with SUBCLASS receivers -> resolve to the fake override; the lambda's `return` is NON-LOCAL, so the
// inline body MUST be spliced at the call site (a plain call would return from the delegate, not the caller).
internal fun probeDerived(d: Derived): Any? = d.firstOr { return "closed" }
internal fun <S : Seg<S>> probeSeg(cur: S): Any? = cur.firstOr { return "seg-closed" }

fun main() {
	val d = Derived()
	d.slot = CLOSED; println(probeDerived(d))         // closed  (non-local return via the fake override)
	d.slot = "D";    println(probeDerived(d))         // D       (else branch)
	val s = ConcreteSeg()
	s.slot = CLOSED; println(probeSeg(s))             // seg-closed
	s.slot = "S";    println(probeSeg(s))             // S
	println(Base().headOrNull() == null)              // True    (own-class self-call, unset)
}
