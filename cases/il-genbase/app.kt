// A NON-generic subclass calling a method INHERITED from a GENERIC base, plus a self-referentially bounded generic
// (`class Segment<S : Segment<S>>`). ilemit's FindMethod returned the OPEN base MethodBuilder (`Base`1::m`), whose
// bare operand is "method/containing type not fully instantiated" at runtime (issue #84 I, the kotlinx.coroutines
// `ConcurrentLinkedListNode<N : ConcurrentLinkedListNode<N>>` / `Segment<S : Segment<S>>` blocker). The inherited
// method must be anchored onto the owner's CONSTRUCTED base instantiation (`Base<Int>` / `Segment<Seg>`).

open class Holder<T>(val v: T) { fun get(): T = v }
class IntHolder(v: Int) : Holder<Int>(v)

abstract class Segment<S : Segment<S>> {
	var next: S? = null
	fun link(n: S) { next = n }
}
class Seg : Segment<Seg>()

fun main() {
	println(IntHolder(42).get())          // inherited generic-base method on a non-generic subclass -> 42

	val a = Seg(); val b = Seg()
	a.link(b)
	println(a.next === b)                  // self-bounded generic field access -> true
	println(b.next == null)                // true
}
