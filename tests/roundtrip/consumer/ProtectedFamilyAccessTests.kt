package roundtriptests.protectedfamily

import NUnit.Framework.TestAttribute
import roundtrip.protectedfamily.FamilyBase
import roundtrip.protectedfamily.FamilyDerived

class ImportedFamily<T>(value: T) : FamilyDerived<T>(value) {
    fun imported(): Int { index = 23; raw = 29; return index + raw }
    fun importedAlias(): Int { val self = this; return self.index + self.raw }
    fun importedCapture(): () -> Int { val delta = 1; return { index + raw + delta } }
    fun importedInline(touch: () -> Unit): () -> Int = capturedInline { touch() }
    fun importedBound(): () -> Int = this::read
    fun <D : ImportedFamily<T>> importedViaBound(other: D): Int = other.index + other.raw
    fun importedFunctions(): Int = transform { it + raw } + callback()
    override fun read(): Int = super.read() + 1000
}

private fun <T> checkImportedFamily(value: T) {
    val imported = ImportedFamily(value)
    check(imported.payload == value && imported.ordinary() == 38)
    check(imported.alias() == 38 && imported.captured()() == 39 && imported.viaBound(imported) == 38)
    check(imported.functions() == 56)
    check(imported.imported() == 52 && imported.importedAlias() == 52)
    check(imported.importedCapture()() == 53 && imported.importedViaBound(imported) == 52)
    check(imported.importedFunctions() == 75)
    check(imported.virtualRead() == 1152 && imported.bound()() == 1152 && imported.importedBound()() == 1152)
    var calls = 0
    val deferred = imported.importedInline { calls++ }
    check(calls == 0 && deferred() == 52 && calls == 1)
}

class ProtectedFamilyAccessTests {
    @TestAttribute fun referencedProtectedFamilyAndGenuineInlineAccessKeepTheirContracts() {
        checkImportedFamily("reference")
        checkImportedFamily(47)
        checkImportedFamily<Int?>(null)
        val reference: FamilyBase<String> = ImportedFamily("base")
        check(reference.virtualRead() == 1136)
    }
}
