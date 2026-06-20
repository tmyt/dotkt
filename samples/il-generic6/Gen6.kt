// Declaration-site variance over reference types -> CLR covariant/contravariant interfaces.
// (Value-type args like Source<Int> do NOT covary to Source<Any> on the CLR — that's a JVM-boxing
//  artifact; CLR reified generics keep them distinct, matching C#. So variance is for reference types.)
interface Producer<out T> {
    fun produce(): T
}
interface Consumer<in T> {
    fun consume(t: T): String
}

class HelloProducer : Producer<String> {
    override fun produce(): String = "hello"
}
class AnyConsumer : Consumer<Any> {
    override fun consume(t: Any): String = "consumed: $t"
}

// Covariance: a Producer<String> flows where Producer<Any> is expected.
fun useProducer(p: Producer<Any>): String = p.produce().toString()
// Contravariance: a Consumer<Any> flows where Consumer<String> is expected.
fun useConsumer(c: Consumer<String>): String = c.consume("world")

fun main() {
    println(useProducer(HelloProducer()))   // hello
    println(useConsumer(AnyConsumer()))      // consumed: world
}
