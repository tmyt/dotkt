package roundtrip.suppression

fun suppressInProducer(primary: Throwable, suppressed: Throwable): Throwable {
    primary.addSuppressed(suppressed)
    return primary
}

fun suppressionSnapshotFromProducer(primary: Throwable): List<Throwable> = primary.suppressedExceptions
