import NUnit.Framework.TestAttribute

class InlineSmartCastOwner<T : Any>(val value: T) {
    private val next = RemoteInlineSmartCastBox<InlineSmartCastOwner<T>?>(this)
    fun copy(): InlineSmartCastOwner<T> = copyImpl()
    private fun copyImpl(): InlineSmartCastOwner<T> {
        next.visitForever { node -> if (node != null) return node }
    }
    fun publicCopy(): InlineSmartCastOwner<T> {
        next.visitForever { node -> if (node != null) return node }
    }
    fun directCopy(): InlineSmartCastOwner<T> = directCopyImpl()
    private fun directCopyImpl(): InlineSmartCastOwner<T> {
        val node = next.value
        if (node != null) return node
        error("missing")
    }
}

private fun <T : Any> selectInlineSmartCast(
    value: InlineSmartCastOwner<T>?, fallback: InlineSmartCastOwner<T>
): InlineSmartCastOwner<T> {
    remoteVisitSmartCast(value) { node -> if (node != null) return node }
    return fallback
}

class InlineSmartCastReturnTests {
    @TestAttribute
    fun nonLocalMemberReturnKeepsConstructedTypeAndIdentity() {
        val integer = InlineSmartCastOwner(42)
        check(integer.copy() === integer)
        check(integer.publicCopy() === integer)
        check(integer.directCopy() === integer)
        check(integer.copy().value == 42)
        val text = InlineSmartCastOwner("value")
        check(text.copy() === text)
        check(text.copy().value == "value")
    }

    @TestAttribute
    fun nonLocalGenericReturnPreservesNullBranchAndSingleEvaluation() {
        val original = InlineSmartCastOwner("original")
        val fallback = InlineSmartCastOwner("fallback")
        var evaluations = 0
        fun read(): InlineSmartCastOwner<String>? { evaluations++; return original }
        check(selectInlineSmartCast(read(), fallback) === original)
        check(evaluations == 1)
        check(selectInlineSmartCast(null, fallback) === fallback)
        check(selectNestedSmartCast(original, fallback) === original)
        check(selectNestedSmartCast(null, fallback) === fallback)
        check(selectReceiverSmartCast(original, fallback) === original)
        check(selectReceiverSmartCast(null, fallback) === fallback)
        check(readNullableSmartCast<Int>(7) == 7)
        check(readNullableSmartCast<Int>(null) == null)
        check(readNullableSmartCast<String>("text") == "text")
        check(readNullableSmartCast<String>(null) == null)
        var evaluationsInsideInline = 0
        remoteEvaluateSmartCast({ evaluationsInsideInline++; original }) { node ->
            check(node === original)
            check(node.value == "original")
        }
        check(evaluationsInsideInline == 1)
    }
}

private fun <T : Any> selectNestedSmartCast(
    value: InlineSmartCastOwner<T>?, fallback: InlineSmartCastOwner<T>
): InlineSmartCastOwner<T> {
    remoteForwardSmartCast(value) { node -> if (node != null) return node }
    return fallback
}

private fun <T> readNullableSmartCast(value: T?): T? {
    remoteForwardSmartCast(value) { node -> return node }
    error("unreachable")
}

private fun <T : Any> selectReceiverSmartCast(
    value: InlineSmartCastOwner<T>?, fallback: InlineSmartCastOwner<T>
): InlineSmartCastOwner<T> {
    remoteReceiverSmartCast(value) { if (this != null) return this }
    return fallback
}
