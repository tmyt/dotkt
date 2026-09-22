import NUnit.Framework.TestAttribute
import protectedstatics.KotlinProtectedStaticBase
import protectedstatics.KotlinProtectedStaticLocalChild
import protectedstatics.KotlinInlineStaticReferenceHolder
import kotlin.coroutines.*

private class KotlinProtectedStaticImportedChild : KotlinProtectedStaticBase() {
    fun exercise() {
        KotlinProtectedStaticImportedChild.value = 19
        check(KotlinProtectedStaticBase.read() == 19)
        val reference = KotlinProtectedStaticImportedChild::increment
        check(reference() == 21)
        KotlinProtectedStaticBase.restricted = 23
        check(KotlinProtectedStaticImportedChild.restricted == 23)
    }
}

class ProtectedStaticRoundtripTests {
    @TestAttribute fun inlineMemberStaticReferencesSurviveMaterialization() {
        val holder = KotlinInlineStaticReferenceHolder()
        check(holder.invoke { f -> f(199) } == 200)
        check(holder.deferred { f -> f(211) }() == 212)
        var actual = 0
        val suspended = holder.suspended { f -> f(223) }
        suspended.startCoroutine(object : Continuation<Int> {
            override val context: CoroutineContext = EmptyCoroutineContext
            override fun resumeWith(result: Result<Int>) { actual = result.getOrThrow() }
        })
        check(actual == 224)
    }
    @TestAttribute fun protectedStaticsSurviveKotlinLibraryReimport() {
        KotlinProtectedStaticLocalChild().exercise()
        KotlinProtectedStaticImportedChild().exercise()
    }
}
