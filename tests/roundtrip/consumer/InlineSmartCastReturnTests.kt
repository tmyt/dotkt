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
    }
}
