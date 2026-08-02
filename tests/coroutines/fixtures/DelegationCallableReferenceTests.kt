// Class-delegation (#81) + callable-reference-adapter (#84 G / receiver-capture) battery (feature fixture). These are not
// coroutine cases, but they migrate into this assembly with the rest of the feature fixture. No blockOn — plain Kotlin
// drive; each old `main` + stdout golden becomes one @TestAttribute method asserting values 1:1 (side effects that
// were `println`'d are captured into a list and asserted positionally).
//
// Coverage preserved (old case -> method):
//   il-classdeleg    -> classDelegation_forwarders            (#81: single/two/expr/generic $$delegate_N fields)
//   il-adapterref    -> adapterRef_memberReferenceCoercion    (#84 G: bound/unbound member ref -> inline forEach)
//   il-capref-inline -> caprefInline_adapterReceiverCapture   (coerced ::ref inside a buildList{} inline lambda)
//
// Top-level names are family-prefixed (`CoroutineDelegationDel`/`CoroutineDelegationdr`/`coroutineDelegationdr`/`coroutineDelegationCap`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

// ---- il-classdeleg: Kotlin CLASS delegation (`class Foo : Bar by baz`) ----------------------------------------
interface CoroutineDelegationDelProducer { fun produce(): String; val tag: Int }
interface CoroutineDelegationDelConsumer { fun consume(s: String): String }

class CoroutineDelegationDelProducerImpl(override val tag: Int) : CoroutineDelegationDelProducer {
    override fun produce() = "p$tag"
}
class CoroutineDelegationDelConsumerImpl : CoroutineDelegationDelConsumer {
    override fun consume(s: String) = "c[$s]"
}
class CoroutineDelegationDelWrap(inner: CoroutineDelegationDelProducer) : CoroutineDelegationDelProducer by inner                       // single delegate
class CoroutineDelegationDelPipe(p: CoroutineDelegationDelProducer, c: CoroutineDelegationDelConsumer) : CoroutineDelegationDelProducer by p, CoroutineDelegationDelConsumer by c  // two delegates
class CoroutineDelegationDelSeeded(seed: Int) : CoroutineDelegationDelProducer by CoroutineDelegationDelProducerImpl(seed * 10)         // delegate to an EXPRESSION
class CoroutineDelegationDelTracked<T>(backing: MutableList<T>) : MutableList<T> by backing               // generic class delegation

// ---- il-adapterref: a coerced MEMBER reference passed to an inline forEach (#84 G) ----------------------------
val coroutineDelegationdrLog = mutableListOf<String>()
class CoroutineDelegationdrSink { fun add(x: Int): Boolean { coroutineDelegationdrLog.add("sink $x"); return true } }   // Boolean member coerced to (Int)->Unit
fun coroutineDelegationdrBuild(src: List<Int>): List<Int> = buildList { src.forEach(::add) }             // UNBOUND ::add vs the buildList receiver

// ---- il-capref-inline: a coerced ::ref inside a buildList{} inline lambda (adapter receiver capture) ----------
fun MutableList<Int>.coroutineDelegationCapPushDouble(x: Int): Boolean { add(x * 2); return true }
fun coroutineDelegationCapCollect(src: List<Int>, bonus: Int): List<Int> = buildList {
    src.forEach(::coroutineDelegationCapPushDouble)   // adapter's ExtensionReceiver = the enclosing buildList `this`
    add(bonus)                          // an ordinary enclosing-local capture into the same lambda
}

class DelegationCallableReferenceTests {
    @TestAttribute
    fun forwarders() {
        val w = CoroutineDelegationDelWrap(CoroutineDelegationDelProducerImpl(1))
        assertEquals("p1", w.produce())   // p1
        assertEquals(1, w.tag)            // 1

        val pipe = CoroutineDelegationDelPipe(CoroutineDelegationDelProducerImpl(2), CoroutineDelegationDelConsumerImpl())
        assertEquals("p2", pipe.produce())                // p2
        assertEquals("c[p2]", pipe.consume(pipe.produce()))// c[p2]
        assertEquals(2, pipe.tag)                          // 2

        val s = CoroutineDelegationDelSeeded(4)
        assertEquals("p40", s.produce())   // p40
        assertEquals(40, s.tag)            // 40

        val t = CoroutineDelegationDelTracked<String>(mutableListOf("a", "b"))
        t.add("c")
        assertEquals(3, t.size)   // 3
        assertEquals("c", t[2])   // c

        val listIterator = t.listIterator()
        assertEquals("a", listIterator.next())
        listIterator.set("A")
        listIterator.add("x")
        assertEquals("x", listIterator.previous())
        listIterator.remove()
        assertEquals(3, t.size)
        assertEquals("A", t[0])
        assertEquals("b", t[1])

        val iterator = t.iterator()
        assertEquals("A", iterator.next())
        iterator.remove()
        assertEquals(2, t.size)
        assertEquals("b", t[0])
        assertEquals("c", t[1])
    }

    @TestAttribute
    fun memberReferenceCoercion() {
        coroutineDelegationdrLog.clear()
        val s = CoroutineDelegationdrSink()
        listOf(1, 2, 3).forEach(s::add)   // BOUND member ref -> inline forEach (side effects: sink 1/2/3)
        assertEquals(3, coroutineDelegationdrLog.size)
        assertEquals("sink 1", coroutineDelegationdrLog[0])
        assertEquals("sink 2", coroutineDelegationdrLog[1])
        assertEquals("sink 3", coroutineDelegationdrLog[2])

        val built = coroutineDelegationdrBuild(listOf(4, 5))   // UNBOUND ::add against the buildList receiver -> [4, 5]
        assertEquals(2, built.size)
        assertEquals(4, built[0])   // built 4
        assertEquals(5, built[1])   // built 5
    }

    @TestAttribute
    fun adapterReceiverCapture() {
        val out = coroutineDelegationCapCollect(listOf(1, 2, 3), 99)
        assertEquals(4, out.size)
        assertEquals(2, out[0])    // 2
        assertEquals(4, out[1])    // 4
        assertEquals(6, out[2])    // 6
        assertEquals(99, out[3])   // 99
    }
}
