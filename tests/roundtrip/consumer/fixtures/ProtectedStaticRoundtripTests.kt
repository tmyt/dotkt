import NUnit.Framework.TestAttribute
import protectedstatics.KotlinProtectedStaticBase
import protectedstatics.KotlinProtectedStaticLocalChild

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
    @TestAttribute fun protectedStaticsSurviveKotlinLibraryReimport() {
        KotlinProtectedStaticLocalChild().exercise()
        KotlinProtectedStaticImportedChild().exercise()
    }
}
