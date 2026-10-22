import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.reflect.KAnnotatedElement
import kotlin.reflect.KCallable
import kotlin.reflect.KClass
import kotlin.reflect.KClassifier
import kotlin.reflect.KDeclarationContainer

annotation class KClassValueAnnotation(val type: KClass<*>)
@KClassValueAnnotation(String::class)
class KClassValueAnnotated

open class KClassValueOtherBase(val label: String)
open class KClassValueManual : KClassValueOtherBase("base"), KClass<Any> {
    override val simpleName: String get() = "Manual"
    override val qualifiedName: String get() = "example.Manual"
    override val annotations: List<Annotation> get() = listOf(KClassValueAnnotation(Int::class))
    override val members: Collection<KCallable<*>> get() = emptyList()
    override fun isInstance(value: Any?): Boolean = value is String
    override fun equals(other: Any?): Boolean = this === other
    override fun hashCode(): Int = 17
}

class KClassValueClassifierOnly : KClassifier
class KClassValueHolder(val type: KClass<*>)
open class KClassValuePlainBase<T>(val expected: T) {
    override fun equals(other: Any?): Boolean = this === other
    override fun hashCode(): Int = 31
    open val simpleName: String? get() = "Plain"
    open val qualifiedName: String? get() = "example.Plain"
    open val annotations: List<Annotation> get() = listOf(KClassValueAnnotation(Int::class))
    open val members: Collection<KCallable<*>> get() = emptyList()
    open fun isInstance(value: Any?): Boolean = value == expected
}
open class KClassValueInherited : KClassValuePlainBase<String>("plain"), KClass<Any>
class KClassValueInheritedOverride : KClassValueInherited() {
    override val simpleName: String get() = "Later"
}
interface KClassValueDefault : KClass<Any> {
    override val simpleName: String get() = "Default"
    override val qualifiedName: String get() = "example.Default"
    override val annotations: List<Annotation> get() = listOf(KClassValueAnnotation(Long::class))
    override val members: Collection<KCallable<*>> get() = emptyList()
    override fun isInstance(value: Any?): Boolean = value is Long
}
class KClassValueDefaultUser : KClassValueDefault {
    override fun equals(other: Any?): Boolean = this === other
    override fun hashCode(): Int = 37
}
abstract class KClassValueAbstract : KClass<Any> {
    override fun equals(other: Any?): Boolean = this === other
    override fun hashCode(): Int = 41
}
class KClassValueDefaultAfterAbstract : KClassValueAbstract(), KClassValueDefault
interface KClassValueOtherOverload {
    fun isInstance(value: KClass<*>): Boolean = false
}
abstract class KClassValueAbstractOverload : KClass<Any>, KClassValueOtherOverload
private fun kClassValueIdentity(value: KClass<*>): KClass<*> = value
private fun <T : KClassifier> kClassifierValueIdentity(value: T): T = value
private inline fun <reified T> kClassValueMatches(value: Any?): Boolean = value is T

class KClassValueTests {
    @TestAttribute
    fun unrelatedGenericClassCanSupplyAllNominalSlots() {
        val value: KClass<*> = KClassValueInherited()
        assertEquals("Plain", value.simpleName)
        assertEquals("example.Plain", value.qualifiedName)
        assertEquals(Int::class, (value.annotations.single() as KClassValueAnnotation).type)
        assertEquals(0, value.members.size)
        assertTrue(value.isInstance("plain"))
        assertEquals(false, value.isInstance(42))
        val later: KClass<*> = KClassValueInheritedOverride()
        assertEquals("Later", later.simpleName)
        assertTrue(later.isInstance("plain"))
    }

    @TestAttribute
    fun selectedInterfaceDefaultsSupplySlotsIncludingThroughAbstractBase() {
        for (value in arrayOf<KClass<*>>(KClassValueDefaultUser(), KClassValueDefaultAfterAbstract())) {
            assertEquals("Default", value.simpleName)
            assertEquals("example.Default", value.qualifiedName)
            assertEquals(Long::class, (value.annotations.single() as KClassValueAnnotation).type)
            assertEquals(0, value.members.size)
            assertTrue(value.isInstance(42L))
            assertEquals(false, value.isInstance(42))
        }
    }

    @TestAttribute
    fun annotationConstructionAndPropertyReadKeepClassIdentity() {
        val value: Any = KClassValueAnnotation(String::class)
        val annotation = value as KClassValueAnnotation
        assertEquals(String::class, annotation.type)
        assertTrue(annotation.type.isInstance("hello"))
        assertEquals(false, annotation.type.isInstance(42))
        val property = KClassValueAnnotation::type
        assertEquals(String::class, property.get(annotation))
        assertEquals(0, property.annotations.size)
        val bound = annotation::type
        assertEquals(String::class, bound.get())
        assertEquals(0, bound.annotations.size)
    }

    @TestAttribute
    fun storedLiteralsValuesAndGenericSlotsHaveOneRepresentation() {
        val values = arrayOf("item")
        val runtimeType = values::class
        assertEquals(values::class, runtimeType)
        val literal: KClass<String> = String::class
        val holder = KClassValueHolder(literal)
        assertTrue(kClassValueIdentity(holder.type) === literal)
        assertTrue(kClassifierValueIdentity(literal) === literal)
        val nullable: KClass<*>? = literal
        assertTrue(nullable === literal)
        assertTrue(literal.isInstance("text"))
    }

    @TestAttribute
    fun ordinaryImplementationsPreserveTheirBaseAndVirtualSlots() {
        val manual = KClassValueManual()
        val value: KClass<*> = manual
        assertEquals("base", manual.label)
        assertTrue(kClassValueIdentity(value) === manual)
        assertEquals("Manual", value.simpleName)
        assertEquals("example.Manual", value.qualifiedName)
        assertTrue(value.isInstance("text"))
        assertEquals(false, value.isInstance(42))
        assertEquals(Int::class, (value.annotations.single() as KClassValueAnnotation).type)
        assertEquals(0, value.members.size)
    }

    @TestAttribute
    fun nativeAndAuthoredClassesRetainAllLogicalParentInterfaces() {
        for (value in arrayOf<KClass<*>>(String::class, KClassValueManual())) {
            val classifier: KClassifier = value
            val annotated: KAnnotatedElement = value
            val container: KDeclarationContainer = value
            assertTrue(kClassifierValueIdentity(classifier) === value)
            assertTrue(annotated === value)
            assertTrue(container === value)
            val erased: Any = value
            assertTrue(erased is KClassifier)
            assertTrue(erased is KAnnotatedElement)
            assertTrue(erased is KDeclarationContainer)
        }
        val classifier: Any = KClassValueClassifierOnly()
        assertTrue(classifier is KClassifier)
        assertEquals(false, classifier is KClass<*>)
    }

    @TestAttribute
    fun classifiersCastsAndArraysRetainLogicalMembership() {
        val native: Any = String::class
        val manual: Any = KClassValueManual()
        assertTrue(kClassValueMatches<KClass<*>>(native))
        assertTrue(kClassValueMatches<KClass<*>>(manual))
        assertTrue(kClassValueMatches<KClassifier>(native))
        assertTrue((native as KClass<*>) === native)
        assertTrue((manual as? KClass<*>) === manual)
        assertEquals(null, "other" as? KClass<*>)
        val empty: Any? = null
        assertEquals(null, empty as KClass<*>?)
        val values: Any = arrayOf<KClass<*>>(String::class, KClassValueManual())
        assertTrue(kClassValueMatches<Array<KClass<*>>>(values))
        val typed = values as Array<KClass<*>>
        assertTrue(typed[0].isInstance("hello"))
        assertEquals("Manual", typed[1].simpleName)
    }

    @TestAttribute
    fun annotationApplicationCarriesClassArgumentThroughClrMetadata() {
        val type = KClassValueAnnotated::class as System.Type
        val attributes = type.GetCustomAttributes(false)
        var found = false
        for (attribute in attributes) {
            if (attribute is KClassValueAnnotation) {
                assertEquals(String::class, attribute.type)
                found = true
            }
        }
        assertTrue(found)
    }
}
