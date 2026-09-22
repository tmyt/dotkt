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
