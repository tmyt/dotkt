// Class-delegation (#81) + callable-reference-adapter (#84 G / receiver-capture) battery (CorA batch). These are not
// coroutine cases, but they migrate into this assembly with the rest of the CorA batch. No blockOn — plain Kotlin
// drive; each old `main` + stdout golden becomes one @TestAttribute method asserting values 1:1 (side effects that
// were `println`'d are captured into a list and asserted positionally).
//
// ILVERIFY NOTE: classdeleg carries a runtime-safe formal-only finding (GitHub #174, same covariance-erasure class as
// #12/#46): the generic class-delegation forwarder narrows MutableList iterator()/listIterator() to the read-only
// Iterator/ListIterator where the Mutable* slot is formally expected. The RUN lane is green; the finding is baselined
// for DotKt.Tests.Coroutines.dll in tests/run-ilverify.sh.
//
// Coverage preserved (old case -> method):
//   il-classdeleg    -> classDelegation_forwarders            (#81: single/two/expr/generic $$delegate_N fields)
//   il-adapterref    -> adapterRef_memberReferenceCoercion    (#84 G: bound/unbound member ref -> inline forEach)
//   il-capref-inline -> caprefInline_adapterReceiverCapture   (coerced ::ref inside a buildList{} inline lambda)
//
// Top-level names are family-prefixed (`CorADel`/`CorAdr`/`corAdr`/`corACap`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

// ---- il-classdeleg: Kotlin CLASS delegation (`class Foo : Bar by baz`) ----------------------------------------
interface CorADelProducer { fun produce(): String; val tag: Int }
interface CorADelConsumer { fun consume(s: String): String }

class CorADelProducerImpl(override val tag: Int) : CorADelProducer {
    override fun produce() = "p$tag"
}
class CorADelConsumerImpl : CorADelConsumer {
    override fun consume(s: String) = "c[$s]"
}
class CorADelWrap(inner: CorADelProducer) : CorADelProducer by inner                       // single delegate
class CorADelPipe(p: CorADelProducer, c: CorADelConsumer) : CorADelProducer by p, CorADelConsumer by c  // two delegates
class CorADelSeeded(seed: Int) : CorADelProducer by CorADelProducerImpl(seed * 10)         // delegate to an EXPRESSION
class CorADelTracked<T>(backing: MutableList<T>) : MutableList<T> by backing               // generic class delegation

// ---- il-adapterref: a coerced MEMBER reference passed to an inline forEach (#84 G) ----------------------------
val corAdrLog = mutableListOf<String>()
class CorAdrSink { fun add(x: Int): Boolean { corAdrLog.add("sink $x"); return true } }   // Boolean member coerced to (Int)->Unit
fun corAdrBuild(src: List<Int>): List<Int> = buildList { src.forEach(::add) }             // UNBOUND ::add vs the buildList receiver

// ---- il-capref-inline: a coerced ::ref inside a buildList{} inline lambda (adapter receiver capture) ----------
fun MutableList<Int>.corACapPushDouble(x: Int): Boolean { add(x * 2); return true }
fun corACapCollect(src: List<Int>, bonus: Int): List<Int> = buildList {
    src.forEach(::corACapPushDouble)   // adapter's ExtensionReceiver = the enclosing buildList `this`
    add(bonus)                          // an ordinary enclosing-local capture into the same lambda
}

class DelegationCallableReferenceTests {
    @TestAttribute
    fun forwarders() {
        val w = CorADelWrap(CorADelProducerImpl(1))
        assertEquals("p1", w.produce())   // p1
        assertEquals(1, w.tag)            // 1

        val pipe = CorADelPipe(CorADelProducerImpl(2), CorADelConsumerImpl())
        assertEquals("p2", pipe.produce())                // p2
        assertEquals("c[p2]", pipe.consume(pipe.produce()))// c[p2]
        assertEquals(2, pipe.tag)                          // 2

        val s = CorADelSeeded(4)
        assertEquals("p40", s.produce())   // p40
        assertEquals(40, s.tag)            // 40

        val t = CorADelTracked<String>(mutableListOf("a", "b"))
        t.add("c")
        assertEquals(3, t.size)   // 3
        assertEquals("c", t[2])   // c
    }

    @TestAttribute
    fun memberReferenceCoercion() {
        corAdrLog.clear()
        val s = CorAdrSink()
        listOf(1, 2, 3).forEach(s::add)   // BOUND member ref -> inline forEach (side effects: sink 1/2/3)
        assertEquals(3, corAdrLog.size)
        assertEquals("sink 1", corAdrLog[0])
        assertEquals("sink 2", corAdrLog[1])
        assertEquals("sink 3", corAdrLog[2])

        val built = corAdrBuild(listOf(4, 5))   // UNBOUND ::add against the buildList receiver -> [4, 5]
        assertEquals(2, built.size)
        assertEquals(4, built[0])   // built 4
        assertEquals(5, built[1])   // built 5
    }

    @TestAttribute
    fun adapterReceiverCapture() {
        val out = corACapCollect(listOf(1, 2, 3), 99)
        assertEquals(4, out.size)
        assertEquals(2, out[0])    // 2
        assertEquals(4, out[1])    // 4
        assertEquals(6, out[2])    // 6
        assertEquals(99, out[3])   // 99
    }
}
