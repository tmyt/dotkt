package protectedstatics

open class KotlinProtectedStaticBase {
    companion {
        protected var value: Int = 3
        protected fun increment(): Int = value + 2
        var restricted: Int = 7
            protected set
        fun read(): Int = value
    }
}

class KotlinProtectedStaticLocalChild : KotlinProtectedStaticBase() {
    fun exercise() {
        KotlinProtectedStaticBase.value = 11
        check(KotlinProtectedStaticLocalChild.increment() == 13)
        KotlinProtectedStaticLocalChild.restricted = 17
        check(KotlinProtectedStaticBase.restricted == 17)
        val reference = KotlinProtectedStaticBase::increment
        check(reference() == 13)
    }
}

class KotlinInlineStaticReferenceHolder {
    companion { fun target(value: Int): Int = value + 1 }
    inline fun invoke(block: ((Int) -> Int) -> Int): Int = block(KotlinInlineStaticReferenceHolder::target)
    inline fun deferred(crossinline block: ((Int) -> Int) -> Int): () -> Int =
        { block(KotlinInlineStaticReferenceHolder::target) }
    inline fun suspended(crossinline block: suspend ((Int) -> Int) -> Int): suspend () -> Int =
        { block(KotlinInlineStaticReferenceHolder::target) }
}
