// Kotlin CLASS delegation (`class Foo : Bar by baz`) — issue #81. The frontend synthesizes a standalone
// `$$delegate_0` IrField (origin DELEGATE, no corresponding property) holding the delegate, plus
// DELEGATED_MEMBER forwarders that read it via GET_FIELD. kotc must emit that field AND run its
// EXPRESSION_BODY initializer in the ctor (like a property backing field); otherwise ilemit fails with
// `field Foo.$$delegate_0 not found`. Shape mirrors the kotlinx.coroutines port (wrapper types that
// forward an interface to a held instance). Covers: single delegate (methods + property forwarding),
// TWO delegates ($$delegate_0/$$delegate_1), a non-param delegate expression, and generic delegation.
interface Producer { fun produce(): String; val tag: Int }
interface Consumer { fun consume(s: String): String }

class ProducerImpl(override val tag: Int) : Producer {
    override fun produce() = "p$tag"
}
class ConsumerImpl : Consumer {
    override fun consume(s: String) = "c[$s]"
}

// single delegate: forwards both a method and a property to $$delegate_0
class Wrap(inner: Producer) : Producer by inner

// two delegates: $$delegate_0 (Producer) + $$delegate_1 (Consumer)
class Pipe(p: Producer, c: Consumer) : Producer by p, Consumer by c

// delegate to an EXPRESSION (not a bare ctor param)
class Seeded(seed: Int) : Producer by ProducerImpl(seed * 10)

// generic class delegation over a stdlib interface
class Tracked<T>(backing: MutableList<T>) : MutableList<T> by backing

fun main() {
    val w = Wrap(ProducerImpl(1))
    println(w.produce())
    println(w.tag)

    val pipe = Pipe(ProducerImpl(2), ConsumerImpl())
    println(pipe.produce())
    println(pipe.consume(pipe.produce()))
    println(pipe.tag)

    val s = Seeded(4)
    println(s.produce())
    println(s.tag)

    val t = Tracked<String>(mutableListOf("a", "b"))
    t.add("c")
    println(t.size)
    println(t[2])
}
