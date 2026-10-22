package roundtrip.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.reflect.KClass
import kotlin.reflect.KClassifier
import roundtrip.kclassvalues.ImportedTyped
import roundtrip.kclassvalues.ImportedManual
import roundtrip.kclassvalues.makeTyped
import roundtrip.kclassvalues.classIdentity
import roundtrip.kclassvalues.annotatedIdentity
import roundtrip.kclassvalues.boundedIdentity
import roundtrip.kclassvalues.matches
import roundtrip.kclassvalues.ImportedPlainBase
import roundtrip.kclassvalues.ImportedDefault

open class KClassValueImportedPlain : ImportedPlainBase<String>("plain"), KClass<Any>
class KClassValueImportedPlainOverride : KClassValueImportedPlain() {
    override val simpleName: String get() = "ImportedLater"
}
class KClassValueImportedDefault : ImportedDefault {
    override fun equals(other: Any?): Boolean = this === other
    override fun hashCode(): Int = 47
}

class KClassValueImportedDerived : ImportedManual() {
    override val simpleName: String get() = "Derived"
    override fun isInstance(value: Any?): Boolean = value is String
}

class KClassValueRoundtripTests {
    @TestAttribute
    fun referencedUnrelatedGenericBaseSatisfiesConsumerInterfaceSlots() {
        val value: KClass<*> = KClassValueImportedPlain()
        assertEquals("ImportedPlain", value.simpleName)
        assertEquals("roundtrip.ImportedPlain", value.qualifiedName)
        assertEquals(0, value.annotations.size)
        assertEquals(0, value.members.size)
        assertTrue(value.isInstance("plain"))
        assertEquals(false, value.isInstance(42))
        val later: KClass<*> = KClassValueImportedPlainOverride()
        assertEquals("ImportedLater", later.simpleName)
        assertTrue(later.isInstance("plain"))
    }

    @TestAttribute
    fun referencedDefaultInterfaceBodiesSatisfyConsumerNominalSlots() {
        val value: KClass<*> = KClassValueImportedDefault()
        assertEquals("ImportedDefault", value.simpleName)
        assertEquals("roundtrip.ImportedDefault", value.qualifiedName)
        assertEquals(0, value.annotations.size)
        assertEquals(0, value.members.size)
        assertTrue(value.isInstance(42L))
        assertEquals(false, value.isInstance(42))
    }

    @TestAttribute
    fun importedAnnotationConstructorsAndGettersRetainKClassSignatures() {
        val value = ImportedTyped(Int::class)
        val type: KClass<*> = value.type
        assertEquals(Int::class, type)
        assertEquals(String::class, makeTyped().type)
        assertTrue(classIdentity(type) === type)
        assertTrue(type.isInstance(42))
    }

    @TestAttribute
    fun literalSourceInterfacesAndWitnessesSurviveDllProjection() {
        val value = String::class
        val classifier: KClassifier = boundedIdentity(value)
        assertTrue(classIdentity(value) === value)
        assertTrue(annotatedIdentity(value) === value)
        assertTrue(classifier === value)
        assertTrue(matches<KClass<*>>(value))
        assertTrue(matches<KClassifier>(value))
        assertEquals(false, matches<KClass<*>>("other"))
    }

    @TestAttribute
    fun importedAndOverriddenOrdinaryImplementationsKeepExactSlots() {
        val imported = ImportedManual()
        val derived = KClassValueImportedDerived()
        val base: KClass<*> = classIdentity(imported)
        val child: KClass<*> = classIdentity(derived)
        assertEquals("base", imported.payload)
        assertEquals("Imported", base.simpleName)
        assertEquals("Derived", child.simpleName)
        assertEquals("roundtrip.Imported", child.qualifiedName)
        assertTrue(base.isInstance(42))
        assertEquals(false, base.isInstance("text"))
        assertTrue(child.isInstance("text"))
        assertEquals(false, child.isInstance(42))
        assertEquals(0, child.annotations.size)
        assertEquals(0, child.members.size)
        assertTrue(boundedIdentity(derived) === derived)
    }
}
