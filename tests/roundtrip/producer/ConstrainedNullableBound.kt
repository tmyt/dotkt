// #345 producer half: the consumer imports this interface and its implementations through the emitted DLL/KLIB.
package roundtrip.constrainedbound

interface ReferencedBoundSink<T> { fun accept(x: T): String }

class ReferencedIntBoundSink : ReferencedBoundSink<Int?> {
    override fun accept(x: Int?): String = "i:" + (x?.toString() ?: "none")
}

class ReferencedAnyBoundSink : ReferencedBoundSink<Any?> {
    override fun accept(x: Any?): String = "a:" + (x?.toString() ?: "none")
}
