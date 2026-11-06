package roundtriptests.suppression

import NUnit.Framework.TestAttribute
import roundtrip.suppression.suppressInProducer
import roundtrip.suppression.suppressionSnapshotFromProducer

class SuppressedExceptionsRoundtripTests {
    @TestAttribute
    fun producerAndConsumerShareSuppressionOnTheSameNativeException() {
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
    }
}
