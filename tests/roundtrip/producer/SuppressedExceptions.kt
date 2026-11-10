package roundtrip.suppression

fun suppressInProducer(primary: Throwable, suppressed: Throwable): Throwable {
    primary.addSuppressed(suppressed)
    return primary
}

fun suppressionSnapshotFromProducer(primary: Throwable): List<Throwable> = primary.suppressedExceptions

open class ProducerCause(private val own: Throwable?, native: Throwable?) : RuntimeException("producer", native) {
    var reads = 0
        private set
    override val cause: Throwable? get() { reads++; return own }
    fun nativeCause(): Throwable? = super.cause
}

class InheritedProducerCause(own: Throwable?, native: Throwable?) : ProducerCause(own, native)
fun causeFromProducer(value: Throwable): Throwable? = value.cause
