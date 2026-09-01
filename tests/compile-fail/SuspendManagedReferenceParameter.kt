import kotlin.clr.ClrRef

suspend fun suspendManagedReferenceParameter(slot: ClrRef<Int>): Int = slot.value
