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
import roundtrip.kclassvalues.ClassLookup
import roundtrip.kclassvalues.ImportedToken
import roundtrip.kclassvalues.capturedName
import GenericValueInterop.ObservedClassLookup

private class ManualToken<Noise, Value : Any>(value: Noise) : ImportedToken<Noise, Value>(value)
private fun capturedNameHere(lookup: ClassLookup, key: KClass<*>): String = lookup.typeName(key)
private fun subtypeName(lookup: ClassLookup, key: ManualToken<Int, *>): String = lookup.typeName(key)
private fun presentName(lookup: ClassLookup, key: KClass<*>?): String = lookup.presentName(key)
private fun capturedValues(lookup: ClassLookup, key: KClass<*>): Any = lookup.emptyValues(key)

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
    private fun checkImportedGenericClassifier() {
        fun token(value: Any) = value::class
        val literal = ImportedPlainBase::class
        val classifier = kotlin.reflect.typeOf<ImportedPlainBase<Int>>().classifier as KClass<*>
        val runtime = token(ImportedPlainBase(1))
        check(mapOf(literal to "registered")[runtime] == "registered")
        check(runtime == classifier && runtime == token(ImportedPlainBase("text")))
        check(runtime == roundtrip.kclassvalues.reflectedLiteral<ImportedPlainBase<String>>())
        check(runtime.annotations.any { it is ImportedTyped })
        check(classifier.simpleName == "ImportedPlainBase" && runtime.qualifiedName == "roundtrip.kclassvalues.ImportedPlainBase")
        check(ImportedPlainBase::class.qualifiedName == runtime.qualifiedName)
        check(literal.isInstance(KClassValueImportedPlain()))
        check(token(KClassValueImportedPlain()) == KClassValueImportedPlain::class)
        class Local<T>(val value: T)
        val local = token(Local(1))
        check(local == token(Local("text")) && local.isInstance(Local("text")))
        check(local.simpleName == "Local" && local.qualifiedName == null)
    }

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
        val property = ImportedTyped::type
        assertEquals(Int::class, property.get(value))
        assertEquals(0, property.annotations.size)
        val bound = value::type
        assertEquals(Int::class, bound.get())
        assertEquals(0, bound.annotations.size)
    }

    @TestAttribute
    fun literalSourceInterfacesAndWitnessesSurviveDllProjection() {
        checkImportedGenericClassifier()
        val value = String::class
        val classifier: KClassifier = boundedIdentity(value)
        assertTrue(classIdentity(value) === value)
        assertTrue(annotatedIdentity(value) === value)
        assertTrue(classifier === value)
        assertTrue(matches<KClass<*>>(value))
        assertTrue(matches<KClassifier>(value))
        assertEquals(false, matches<KClass<*>>("other"))
        val lookup: ClassLookup = ObservedClassLookup()
        val custom = ManualToken<Int, String>(42)
        assertEquals("System.Int32", capturedName(lookup, Int::class))
        val native: Any = ArrayList<Int>()
        assertEquals(ObservedClassLookup.RuntimeTypeName(native), capturedName(lookup, native::class))
        assertEquals("System.String", capturedName(lookup, custom))
        assertEquals("System.Int32", capturedNameHere(lookup, Int::class))
        assertEquals("System.String", capturedNameHere(lookup, custom))
        assertEquals("System.String", subtypeName(lookup, custom))
        assertEquals("System.Int32", presentName(lookup, Int::class))
        assertTrue(ObservedClassLookup.SameRuntimeType(lookup.emptyValues(String::class), capturedValues(lookup, String::class)))
        assertTrue(ObservedClassLookup.SameRuntimeType(lookup.emptyValues(Int::class), capturedValues(lookup, Int::class)))
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
