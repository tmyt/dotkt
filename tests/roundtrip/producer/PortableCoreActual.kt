package roundtrip.portablecoreactual

fun importedCloseable(onClose: () -> Unit): AutoCloseable = AutoCloseable { onClose() }

fun importedNullablePublication(): Lazy<String?> = lazy(LazyThreadSafetyMode.PUBLICATION) { null }

fun importedUseAssignment(): String {
    val assigned: String
    AutoCloseable {}.use { assigned = "assigned in producer" }
    return assigned
}
