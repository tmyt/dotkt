package roundtriptests.suppression

import NUnit.Framework.TestAttribute
import DotKt.Tests.Suppression.SuppressionGcProbe
import roundtrip.suppression.suppressInProducer
import roundtrip.suppression.suppressionSnapshotFromProducer
import roundtrip.suppression.ProducerCause
import roundtrip.suppression.InheritedProducerCause
import roundtrip.suppression.causeFromProducer

private class ConsumerCause(private val local: Throwable?, parent: Throwable?, native: Throwable?) : ProducerCause(parent, native) {
    override val cause: Throwable? get() = local
    fun parentCause(): Throwable? = super.cause
}

class SuppressedExceptionsRoundtripTests {
    @TestAttribute
    fun aSuppressedBackReferenceDoesNotKeepThePrimaryAlive() {
        val weak = SuppressionGcProbe.CreateCycle(System.Action2<System.Exception, System.Exception> { primary, child ->
            val error = primary as Throwable
            val suppressed = child as Throwable
            error.addSuppressed(suppressed)
            check(error.suppressedExceptions.single() === suppressed)
        })
        check(SuppressionGcProbe.AreCollected(weak))
    }

    @TestAttribute
    fun producerAndConsumerShareSuppressionAndCauseOnTheSameNativeException() {
        val primary = IllegalStateException("primary")
        val producerChild = IllegalArgumentException("producer")
        val consumerChild = RuntimeException("consumer")
        check(suppressInProducer(primary, producerChild) === primary)
        check(primary.suppressedExceptions.single() === producerChild)
        val before = suppressionSnapshotFromProducer(primary)
        primary.addSuppressed(consumerChild)
        val after = suppressionSnapshotFromProducer(primary)
        check(before.size == 1 && before[0] === producerChild)
        check(after.size == 2 && after[0] === producerChild && after[1] === consumerChild)
        check(suppressInProducer(primary, primary) === primary)
        check(suppressionSnapshotFromProducer(primary).size == 2)
        check(suppressionSnapshotFromProducer(RuntimeException("independent")).isEmpty())

        val own = IllegalArgumentException("own-cause")
        val native = RuntimeException("native-cause")
        val producer = ProducerCause(own, native)
        check((producer as Throwable).cause === own && producer.reads == 1)
        check(causeFromProducer(producer) === own && producer.reads == 2)
        check(producer.nativeCause() === native && producer.reads == 2)
        val inherited = InheritedProducerCause(own, native)
        check(causeFromProducer(inherited) === own)
        val consumer = ConsumerCause(null, own, native)
        check((consumer as Throwable).cause == null && causeFromProducer(consumer) == null)
        check(consumer.parentCause() === own && consumer.reads == 1)
        check(consumer.nativeCause() === native && consumer.reads == 1)
        val caught = try { throw consumer } catch (failure: Throwable) { failure }
        check(caught === consumer && caught.cause == null)
        val suppressedCause = RuntimeException("logical-cause-suppression")
        own.addSuppressed(suppressedCause)
        native.addSuppressed(RuntimeException("native-only-suppression"))
        val rendered = producer.stackTraceToString()
        check(rendered.contains("logical-cause-suppression") && rendered.contains("own-cause"))
        check(!rendered.contains("native-only-suppression"))
    }
}
