// #395 — the frontend-selected declaration must survive CLR type erasure. Each pair below is a legal Kotlin
// overload set but occupies one CLI signature. The source supplies the distinct physical names explicitly; bir2cir
// binds every declaration and use from the frontend identity and rejects any collision left unresolved.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine
import kotlin.math.sin
import kotlin.reflect.KProperty

private val Map<Int, Int>.identityProperty: Int get() = 11
@get:kotlin.clr.ClrName("identityPropertyMutableMap")
private val MutableMap<Int, Int>.identityProperty: Int get() = 22
private val String.identityNullableProperty: Int get() = 33
@get:kotlin.clr.ClrName("identityNullablePropertyNullable")
private val String?.identityNullableProperty: Int get() = 44

private var mapIdentityState: Int = 0
private var mutableMapIdentityState: Int = 0
private var Map<Int, Int>.identityMutableProperty: Int
    get() = mapIdentityState
    set(value) { mapIdentityState = value + 400 }
@get:kotlin.clr.ClrName("identityMutablePropertyMutableMap")
@set:kotlin.clr.ClrName("identityMutablePropertyMutableMap")
private var MutableMap<Int, Int>.identityMutableProperty: Int
    get() = mutableMapIdentityState
    set(value) { mutableMapIdentityState = value + 500 }

private fun Map<Int, Int>.identityFunction(): Int = 55
@kotlin.clr.ClrName("identityFunctionMutableMap")
private fun MutableMap<Int, Int>.identityFunction(): Int = 66
private fun String.identityNullableFunction(): Int = 77
@kotlin.jvm.JvmName("identityNullableFunctionNullable")
private fun String?.identityNullableFunction(): Int = 88
private fun physicalObjectCollision(value: Any): Int = 89
@kotlin.clr.ClrName("physicalObjectCollisionSuspend")
private fun physicalObjectCollision(value: suspend () -> Unit): Int = 90
private val Any.physicalObjectPropertyCollision: Int get() = 91
@get:kotlin.clr.ClrName("physicalObjectPropertyCollisionSuspend")
private val (suspend () -> Unit).physicalObjectPropertyCollision: Int get() = 92
@kotlin.clr.ClrName("coalescedExplicitName")
@kotlin.jvm.JvmName("coalescedExplicitName")
private fun identicalExplicitAliases(): Int = 121
@get:kotlin.clr.ClrName("explicitDefaultPropertyPhysical")
private val explicitDefaultProperty: Int = 122

// Same collision families in reverse declaration order. Stable physical allocation must not depend on which body
// happened to be visited first.
@kotlin.clr.ClrName("reverseIdentityFunctionMutableMap")
private fun MutableMap<Int, Int>.reverseIdentityFunction(): Int = 102
private fun Map<Int, Int>.reverseIdentityFunction(): Int = 101
@get:kotlin.clr.ClrName("reverseIdentityPropertyNullable")
private val String?.reverseIdentityProperty: Int get() = 104
private val String.reverseIdentityProperty: Int get() = 103

private class FinalMemberIdentity {
    fun Map<Int, Int>.erasedMember(): Int = 105
    @kotlin.clr.ClrName("erasedMemberMutableMap")
    fun MutableMap<Int, Int>.erasedMember(): Int = 106

    fun read(value: Map<Int, Int>): Int = value.erasedMember()
    fun readMutable(value: MutableMap<Int, Int>): Int = value.erasedMember()
    fun erasedCallable(value: Map<Int, Int>): Int = value.size + 107
    @kotlin.clr.ClrName("erasedCallableMutableMap")
    fun erasedCallable(value: MutableMap<Int, Int>): Int = value.size + 108
}

private class StaticMemberIdentity {
    companion {
        fun erasedCallable(value: Map<Int, Int>): Int = value.size + 109
        @kotlin.clr.ClrName("erasedCallableMutableMap")
        fun erasedCallable(value: MutableMap<Int, Int>): Int = value.size + 110
        suspend fun erasedSuspendCallable(value: Map<Int, Int>): Int = value.size + 111
        @kotlin.clr.ClrName("erasedSuspendCallableMutableMap")
        suspend fun erasedSuspendCallable(value: MutableMap<Int, Int>): Int = value.size + 112
    }
}

private class ObjectCompanionMemberIdentity {
    companion object {
        fun erasedCallable(value: Map<Int, Int>): Int = value.size + 116
        @kotlin.clr.ClrName("erasedCallableMutableMap")
        fun erasedCallable(value: MutableMap<Int, Int>): Int = value.size + 117
    }
}

private class LocalErasedDelegate {
    var setterResult: Int = 0

    operator fun getValue(thisRef: Map<Int, Int>?, property: KProperty<*>): Int = 119
    @kotlin.clr.ClrName("getValueMutableMap")
    operator fun getValue(thisRef: MutableMap<Int, Int>?, property: KProperty<*>): Int = 120
    operator fun setValue(thisRef: Map<Int, Int>?, property: KProperty<*>, value: Int) {
        setterResult = value + 800
    }
    @kotlin.clr.ClrName("setValueMutableMap")
    operator fun setValue(thisRef: MutableMap<Int, Int>?, property: KProperty<*>, value: Int) {
        setterResult = value + 900
    }
}

private fun runImmediate(block: suspend () -> Int): Int {
    var outcome: Result<Int>? = null
    block.startCoroutine(object : Continuation<Int> {
        override val context: CoroutineContext get() = EmptyCoroutineContext
        override fun resumeWith(result: Result<Int>) { outcome = result }
    })
    return outcome!!.getOrThrow()
}

internal class CompanionExtensionIdentity

internal companion fun CompanionExtensionIdentity.erasedCompanionCallable(value: Map<Int, Int>): Int =
    value.size + 113
@kotlin.clr.ClrName("erasedCompanionCallableMutableMap")
internal companion fun CompanionExtensionIdentity.erasedCompanionCallable(value: MutableMap<Int, Int>): Int =
    value.size + 114
@get:kotlin.clr.ClrName("explicitCompanionPropertyPhysical")
internal companion val CompanionExtensionIdentity.explicitCompanionProperty: Int get() = 119
private var explicitCompanionPropertyState: Int = 0
@get:kotlin.clr.ClrName("explicitCompanionMutableGet")
@set:kotlin.clr.ClrName("explicitCompanionMutableSet")
internal companion var CompanionExtensionIdentity.explicitCompanionMutableProperty: Int
    get() = explicitCompanionPropertyState
    set(value) { explicitCompanionPropertyState = value }
@get:kotlin.clr.ClrName("explicitCompanionFieldGet")
@set:kotlin.clr.ClrName("explicitCompanionFieldSet")
internal companion var CompanionExtensionIdentity.explicitCompanionFieldProperty: Int = 0


class DeclarationIdentityTests {
    @TestAttribute
    fun erasedExtensionPropertiesKeepSelectedDeclaration() {
        val readOnly: Map<Int, Int> = mapOf(1 to 1)
        val mutable: MutableMap<Int, Int> = mutableMapOf(1 to 1)
        val present: String = "x"
        val optional: String? = null
        assertEquals(11, readOnly.identityProperty)
        assertEquals(22, mutable.identityProperty)
        assertEquals(33, present.identityNullableProperty)
        assertEquals(44, optional.identityNullableProperty)
        readOnly.identityMutableProperty = 9
        mutable.identityMutableProperty = 10
        assertEquals(409, readOnly.identityMutableProperty)
        assertEquals(510, mutable.identityMutableProperty)
    }

    @TestAttribute
    fun erasedExtensionFunctionsKeepSelectedDeclaration() {
        val readOnly: Map<Int, Int> = mapOf(1 to 1)
        val mutable: MutableMap<Int, Int> = mutableMapOf(1 to 1)
        val present: String = "x"
        val optional: String? = null
        assertEquals(55, readOnly.identityFunction())
        assertEquals(66, mutable.identityFunction())
        assertEquals(77, present.identityNullableFunction())
        assertEquals(88, optional.identityNullableFunction())
        assertEquals(121, identicalExplicitAliases())
        assertEquals(122, explicitDefaultProperty)
        val defaultPropertyRef = ::explicitDefaultProperty
        assertEquals(122, defaultPropertyRef.get())
        assertEquals(101, readOnly.reverseIdentityFunction())
        assertEquals(102, mutable.reverseIdentityFunction())
        assertEquals(103, present.reverseIdentityProperty)
        assertEquals(104, optional.reverseIdentityProperty)
    }

    @TestAttribute
    fun physicalObjectSpellingsStillAllocateOneCollisionSet() {
        val block: suspend () -> Unit = {}
        assertEquals(89, physicalObjectCollision("plain object"))
        assertEquals(90, physicalObjectCollision(block))
        assertEquals(91, "plain object".physicalObjectPropertyCollision)
        assertEquals(92, block.physicalObjectPropertyCollision)
    }

    @TestAttribute
    fun callableReferencesKeepSelectedDeclaration() {
        val readOnly: Map<Int, Int> = mapOf(1 to 1)
        val mutable: MutableMap<Int, Int> = mutableMapOf(1 to 1)
        val readFunction: (Map<Int, Int>) -> Int = Map<Int, Int>::identityFunction
        val mutableFunction: (MutableMap<Int, Int>) -> Int = MutableMap<Int, Int>::identityFunction
        val readProperty = Map<Int, Int>::identityProperty
        val mutableProperty = MutableMap<Int, Int>::identityProperty
        val readMutableProperty = Map<Int, Int>::identityMutableProperty
        val mutableMutableProperty = MutableMap<Int, Int>::identityMutableProperty
        assertEquals(55, readFunction(readOnly))
        assertEquals(66, mutableFunction(mutable))
        assertEquals(11, readProperty.get(readOnly))
        assertEquals(22, mutableProperty.get(mutable))
        readMutableProperty.set(readOnly, 11)
        mutableMutableProperty.set(mutable, 12)
        assertEquals(411, readMutableProperty.get(readOnly))
        assertEquals(512, mutableMutableProperty.get(mutable))
        val pair = 1 to 2
        assertEquals(2, mapOf(pair)[1])
        val singletonMap: (Pair<Int, Int>) -> Map<Int, Int> = ::mapOf
        assertEquals(2, singletonMap(pair)[1])
        val sinRef: (Double) -> Double = ::sin
        assertEquals(0.0, sinRef(0.0))
    }

    @TestAttribute
    fun crossFileCallsKeepSelectedDeclaration() {
        val readOnly: Map<Int, Int> = mapOf(1 to 1)
        val mutable: MutableMap<Int, Int> = mutableMapOf(1 to 1)
        assertEquals(201, readOnly.crossFileIdentity())
        assertEquals(202, mutable.crossFileIdentity())
        assertEquals(203, readOnly.crossFileIdentityProperty)
        assertEquals(204, mutable.crossFileIdentityProperty)
    }

    @TestAttribute
    fun finalMemberExtensionsKeepSelectedDeclaration() {
        val owner = FinalMemberIdentity()
        val readOnly: Map<Int, Int> = mapOf(1 to 1)
        val mutable: MutableMap<Int, Int> = mutableMapOf(1 to 1)
        assertEquals(105, owner.read(readOnly))
        assertEquals(106, owner.readMutable(mutable))
        val readRef: (Map<Int, Int>) -> Int = owner::erasedCallable
        val mutableRef: (MutableMap<Int, Int>) -> Int = owner::erasedCallable
        assertEquals(108, readRef(readOnly))
        assertEquals(109, mutableRef(mutable))
    }

    @TestAttribute
    fun staticMemberCallableReferencesKeepSelectedDeclaration() {
        val readOnly: Map<Int, Int> = mapOf(1 to 1)
        val mutable: MutableMap<Int, Int> = mutableMapOf(1 to 1)
        assertEquals(110, StaticMemberIdentity.erasedCallable(readOnly))
        assertEquals(111, StaticMemberIdentity.erasedCallable(mutable))
        val readRef: (Map<Int, Int>) -> Int = StaticMemberIdentity::erasedCallable
        val mutableRef: (MutableMap<Int, Int>) -> Int = StaticMemberIdentity::erasedCallable
        assertEquals(110, readRef(readOnly))
        assertEquals(111, mutableRef(mutable))
        val readSuspendRef: suspend (Map<Int, Int>) -> Int = StaticMemberIdentity::erasedSuspendCallable
        val mutableSuspendRef: suspend (MutableMap<Int, Int>) -> Int = StaticMemberIdentity::erasedSuspendCallable
        assertEquals(112, runImmediate { readSuspendRef(readOnly) })
        assertEquals(113, runImmediate { mutableSuspendRef(mutable) })
        assertEquals(117, ObjectCompanionMemberIdentity.erasedCallable(readOnly))
        assertEquals(118, ObjectCompanionMemberIdentity.erasedCallable(mutable))
        val objectReadRef: (Map<Int, Int>) -> Int = ObjectCompanionMemberIdentity::erasedCallable
        val objectMutableRef: (MutableMap<Int, Int>) -> Int = ObjectCompanionMemberIdentity::erasedCallable
        assertEquals(117, objectReadRef(readOnly))
        assertEquals(118, objectMutableRef(mutable))
    }

    @TestAttribute
    fun companionExtensionCallableReferencesKeepSelectedDeclaration() {
        val readOnly: Map<Int, Int> = mapOf(1 to 1)
        val mutable: MutableMap<Int, Int> = mutableMapOf(1 to 1)
        assertEquals(114, CompanionExtensionIdentity.erasedCompanionCallable(readOnly))
        assertEquals(115, CompanionExtensionIdentity.erasedCompanionCallable(mutable))
        val readRef: (Map<Int, Int>) -> Int = CompanionExtensionIdentity::erasedCompanionCallable
        val mutableRef: (MutableMap<Int, Int>) -> Int = CompanionExtensionIdentity::erasedCompanionCallable
        assertEquals(114, readRef(readOnly))
        assertEquals(115, mutableRef(mutable))
        assertEquals(119, CompanionExtensionIdentity.explicitCompanionProperty)
        val propertyRef = CompanionExtensionIdentity::explicitCompanionProperty
        assertEquals(119, propertyRef.get())
        CompanionExtensionIdentity.explicitCompanionMutableProperty = 123
        assertEquals(123, CompanionExtensionIdentity.explicitCompanionMutableProperty)
        val mutablePropertyRef = CompanionExtensionIdentity::explicitCompanionMutableProperty
        mutablePropertyRef.set(124)
        assertEquals(124, mutablePropertyRef.get())
        CompanionExtensionIdentity.explicitCompanionFieldProperty = 125
        assertEquals(125, CompanionExtensionIdentity.explicitCompanionFieldProperty)
        val fieldPropertyRef = CompanionExtensionIdentity::explicitCompanionFieldProperty
        fieldPropertyRef.set(126)
        assertEquals(126, fieldPropertyRef.get())
        val genericSlots = GenericParameterSignatureProbe<Int, String>()
        assertEquals(1, genericSlots.select(1))
        assertEquals(2, genericSlots.select("x"))
    }

    @TestAttribute
    fun nullableGenericErasureKeepsSelectedDeclarationOnConcreteAndStarReceivers() {
        val concrete = NullableGenericIdentityProbe<Int>()
        assertEquals(3, concrete.select(3))
        assertEquals(4, concrete.select("s"))

        val star: NullableGenericIdentityProbe<*> = concrete
        assertEquals(4, star.select("s"))

        val independent = StarIndependentIdentityProbe<Int>()
        val independentStar: StarIndependentIdentityProbe<*> = independent
        val readOnly: Map<Int, Int> = mapOf(1 to 1)
        val mutable: MutableMap<Int, Int> = mutableMapOf(1 to 1)
        assertEquals(5, independent.select(readOnly))
        assertEquals(6, independent.select(mutable))
        assertEquals(5, independentStar.select(readOnly))
        assertEquals(6, independentStar.select(mutable))
        val starReadRef: (Map<Int, Int>) -> Int = independentStar::select
        val starMutableRef: (MutableMap<Int, Int>) -> Int = independentStar::select
        assertEquals(5, starReadRef(readOnly))
        assertEquals(6, starMutableRef(mutable))
    }

    @TestAttribute
    fun localDelegatedPropertyKeepsSelectedOperatorDeclaration() {
        val delegate = LocalErasedDelegate()
        val read by delegate
        assertEquals(120, read)
        var written by delegate
        written = 7
        assertEquals(907, delegate.setterResult)
    }
}
