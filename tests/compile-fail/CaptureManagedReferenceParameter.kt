import kotlin.clr.ClrRef

fun captureManagedReferenceParameter(slot: ClrRef<Int>): () -> Int = {
    slot.value
}
