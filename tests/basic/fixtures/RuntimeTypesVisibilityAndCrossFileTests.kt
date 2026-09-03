// Language-core battery (feature fixture): boxing/reassign, super-calls, star-projection casts, visibility, typealias,
// and cross-file/cross-namespace dispatch + a cross-file top-level property. Migrates the core-language family of
// cases/il-* onto the in-process NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute
// method whose per-value assert is strictly stronger (typed) than the old text diff; every asserted value is
// preserved 1:1 (see `// <expected>`).
//
// Coverage preserved (old case -> method):
//   il-setlocalbox -> localBox_anyReassign            `Any` local/field reassigned across value/reference boxing
//   il-supercall   -> superCall_nonVirtual            #14 super.X() = a NON-virtual call to the resolved base slot (method/prop/3-level/DIM)
//   il-starproj    -> starProjection_nonGenericFacade #60 value-type-arg collection erased to Any + `is Map<*,*>` -> NON-generic BCL facade
//   il-vis         -> visibilityModifiers             private/internal/protected members + a private top-level fun
//   il-typealias   -> typealias_acrossBoundary        typealias over stdlib-generic / function-type / user-class across a fn boundary
//   il-xfaceimpl   -> crossFileIfaceDispatch          cross-file + namespaced interface impl/dispatch (declarations in CrossFileLanguageSupport.kt)
//   il-xprop       -> crossFileTopLevelProp           mutable top-level property declared in a sibling file, read + written here
//
// All top-level declarations introduced here are RuntimeTypes-prefixed (one assembly = one namespace). The cross-file cases'
// sibling declarations live in CrossFileLanguageSupport.kt (package crossFileLanguage).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import System.Type
import crossFileLanguage.CrossFileLanguageImplementation
import crossFileLanguage.crossFileLanguageCurrent
import crossFileLanguage.crossFileLanguageCall
import crossFileLanguage.crossFileLanguageCounter
import crossFileLanguage.crossFileLanguageBump
import kotlin.clr.ClrRef
import kotlin.clr.byref

private interface RuntimeTypesPrivateInterface
internal interface RuntimeTypesInternalInterface
interface RuntimeTypesPublicInterface
@PublishedApi internal interface RuntimeTypesPublishedInterface

private annotation class RuntimeTypesPrivateAnnotation
internal annotation class RuntimeTypesInternalAnnotation
annotation class RuntimeTypesPublicAnnotation
@PublishedApi internal annotation class RuntimeTypesPublishedAnnotation
@PublishedApi internal class RuntimeTypesPublishedClass
@PublishedApi internal object RuntimeTypesPublishedObject
@PublishedApi internal enum class RuntimeTypesPublishedEnum { Value }
@PublishedApi internal enum class RuntimeTypesPublishedRichEnum(val code: Int) {
    Value(1);
    fun value(): Int = code
}

class RuntimeTypesVisibilityOwner {
    private interface PrivateNestedInterface
    internal interface InternalNestedInterface
    protected interface ProtectedNestedInterface
    interface PublicNestedInterface

    private annotation class PrivateNestedAnnotation
    internal annotation class InternalNestedAnnotation
    protected annotation class ProtectedNestedAnnotation
    annotation class PublicNestedAnnotation
}

object RuntimeTypesAnnotationObjectOwner {
    internal annotation class InternalNestedAnnotation

    class DepthOne {
        private annotation class PrivateDeepAnnotation

        @PrivateDeepAnnotation
        class AnnotatedDeepTarget
    }
}

interface RuntimeTypesAnnotationInterfaceOwner {
    annotation class PublicNestedAnnotation
}

@RuntimeTypesVisibilityOwner.PublicNestedAnnotation
@RuntimeTypesAnnotationObjectOwner.InternalNestedAnnotation
@RuntimeTypesAnnotationInterfaceOwner.PublicNestedAnnotation
class RuntimeTypesNestedAnnotationTarget

// ---- il-setlocalbox : `Any` field reassigned from String to a boxed Int -------------------------------------------
class RuntimeTypesHolder {
    var v: Any = "s"
    fun put(n: Int) { v = n }
}

// ---- il-supercall : #14 super.X() from an override = a non-virtual call to the resolved base slot -----------------
open class RuntimeTypesBase {
    open fun greet() = "base"
    open fun twice(x: Int) = x * 2
    open val tag: String get() = "base-tag"
    open fun describe() = "Base"
}
class RuntimeTypesDerived : RuntimeTypesBase() {
    override fun greet() = "derived+" + super.greet()
    override fun twice(x: Int) = super.twice(x) + 1
    override val tag: String get() = "derived[" + super.tag + "]"
    override fun describe() = "Derived<" + super.describe() + ">"
}

// An immediate super class can inherit the concrete implementation while also stating an interface carrying the
// same abstract slot. `super` must call the concrete base-class MethodDef, never the interface obligation.
interface RuntimeTypesSuperMethodSlot { fun inheritedMethod(value: String): String }
interface RuntimeTypesSuperMethodFace : RuntimeTypesSuperMethodSlot
open class RuntimeTypesSuperMethodBase : RuntimeTypesSuperMethodSlot {
    override fun inheritedMethod(value: String): String = "method-base:$value"
    fun inheritedMethod(value: Int): String = "wrong-overload:$value"
}
open class RuntimeTypesSuperMethodMiddle : RuntimeTypesSuperMethodBase(), RuntimeTypesSuperMethodFace
class RuntimeTypesSuperMethodDerived : RuntimeTypesSuperMethodMiddle() {
    override fun inheritedMethod(value: String): String = "method-derived>" + super.inheritedMethod(value)
}

interface RuntimeTypesSuperPropertySlot { val inheritedProperty: String }
interface RuntimeTypesSuperPropertyFace : RuntimeTypesSuperPropertySlot
open class RuntimeTypesSuperPropertyBase : RuntimeTypesSuperPropertySlot {
    override val inheritedProperty: String get() = "property-base"
}
open class RuntimeTypesSuperPropertyMiddle : RuntimeTypesSuperPropertyBase(), RuntimeTypesSuperPropertyFace
class RuntimeTypesSuperPropertyDerived : RuntimeTypesSuperPropertyMiddle() {
    override val inheritedProperty: String get() = "property-derived>" + super.inheritedProperty
}

// Non-null parameter checks belong to executable entries. Abstract methods/accessors declare slots only and must
// stay bodyless in BIR/CIR; the artifact assertion paired with this fixture pins both declaration families.
abstract class RuntimeTypesAbstractBodylessContract {
    abstract fun transform(value: String): String
    abstract var text: String
}

open class RuntimeTypesA { open fun name() = "A" }
open class RuntimeTypesB : RuntimeTypesA() { override fun name() = super.name() + "B" }
class RuntimeTypesC : RuntimeTypesB() { override fun name() = super.name() + "C" }
open class RuntimeTypesAnimal { override fun toString() = "animal" }
class RuntimeTypesDog : RuntimeTypesAnimal() { override fun toString() = "dog>" + super.toString() }
interface RuntimeTypesGreeter { fun hi(): String = "hi-default" }
class RuntimeTypesImpl : RuntimeTypesGreeter { override fun hi() = "impl+" + super.hi() }

open class RuntimeTypesProtectedSuperBase {
    protected open fun label(): String = "base"
}
class RuntimeTypesProtectedSuperDerived : RuntimeTypesProtectedSuperBase() {
    override fun label(): String = "derived"
    fun inlineSuper(): String = run { super.label() }
    fun liftedSuper(): () -> String = { super.label() }
}
class RuntimeTypesSuperContext(val prefix: String)
interface RuntimeTypesGenericMarker<T> { fun render(): String }
class RuntimeTypesStringMarker : RuntimeTypesGenericMarker<String> { override fun render(): String = "marker" }
private interface RuntimeTypesExistentialFlow<T>
private interface RuntimeTypesExistentialFusibleFlow<T> : RuntimeTypesExistentialFlow<T> {
    fun fuse(): RuntimeTypesExistentialFlow<T>
}
private class RuntimeTypesExistentialFlowImpl<T> : RuntimeTypesExistentialFusibleFlow<T> {
    override fun fuse(): RuntimeTypesExistentialFlow<T> = this
}
private fun <T> runtimeTypesFuse(flow: RuntimeTypesExistentialFlow<T>): RuntimeTypesExistentialFlow<T> =
    when (flow) {
        is RuntimeTypesExistentialFusibleFlow -> flow.fuse()
        else -> flow
    }
open class RuntimeTypesGenericProtectedSuperBase<T> {
    context(context: RuntimeTypesSuperContext)
    protected open fun <U : RuntimeTypesGenericMarker<T>> combine(value: T, marker: U): String =
        context.prefix + ":" + value.toString() + ":" + marker.render()
    protected open fun echo(value: T): T = value
}
class RuntimeTypesGenericProtectedSuperDerived<A, B> : RuntimeTypesGenericProtectedSuperBase<B>() {
    context(context: RuntimeTypesSuperContext)
    override fun <U : RuntimeTypesGenericMarker<B>> combine(value: B, marker: U): String = "derived"
    override fun echo(value: B): B = value
    context(context: RuntimeTypesSuperContext)
    fun <U : RuntimeTypesGenericMarker<B>> liftedSuper(value: B, marker: U): () -> String =
        { super.combine(super.echo(value), marker) }
}

// ---- il-vis : visibility modifiers -> CLR access flags ------------------------------------------------------------
class RuntimeTypesAccount(private val balance: Int) {
    private fun fee(): Int = 2
    fun net(): Int = balance - fee()
    internal fun tag(): String = "acct"
    protected open fun kind(): String = "base"
}
private fun runtimeTypessecret(): Int = 99

// #225: accessor-routed properties keep a private CLR backing field. This frontend-valid address edge originates on
// the file facade and targets the sibling class TypeDef, so bir2cir must synthesize a caller-owned UnsafeAccessor
// instead of kotc widening the slot. The test below executes the edge so the runtime's name/signature binding is
// covered in addition to compile/ILVerify.
private class RuntimeTypesByRefOwner(var slot: Int)
private fun runtimeTypesTakeByRef(slot: ClrRef<Int>) {}
private fun runtimeTypesPrivateBackingAddress(owner: RuntimeTypesByRefOwner) {
    runtimeTypesTakeByRef(byref(owner.slot))
}

// ---- il-typealias : aliases used across a function boundary -------------------------------------------------------
typealias RuntimeTypesNames = List<String>
typealias RuntimeTypesIntOp = (Int) -> Int
typealias RuntimeTypesPairs = Map<String, Int>
class RuntimeTypesTaBox(val v: Int) { fun twice(): Int = v * 2 }
typealias RuntimeTypesContainer = RuntimeTypesTaBox
fun runtimeTypesjoin(ns: RuntimeTypesNames): String = ns.joinToString(",")
fun runtimeTypesmakeNames(): RuntimeTypesNames = listOf("a", "b", "c")
fun runtimeTypesapply2(op: RuntimeTypesIntOp, x: Int): Int = op(op(x))
fun runtimeTypesunwrap(c: RuntimeTypesContainer): Int = c.twice()
fun runtimeTypeslookup(p: RuntimeTypesPairs, k: String): Int = p[k] ?: -1

class RuntimeTypeAndSuperDispatchTests {
    @TestAttribute
    fun anyReassign() {
        var a: Any = "x"
        a = 42
        assertEquals(42, a)          // 42
        val h = RuntimeTypesHolder()
        h.put(7)
        assertEquals(7, h.v)         // 7
    }

    @TestAttribute
    fun nonVirtual() {
        val d = RuntimeTypesDerived()
        assertEquals("derived+base", d.greet())        // derived+base
        assertEquals(21, d.twice(10))                   // 21
        assertEquals("derived[base-tag]", d.tag)        // derived[base-tag]
        assertEquals("Derived<Base>", d.describe())     // Derived<Base>
        assertEquals("ABC", RuntimeTypesC().name())               // ABC
        assertEquals("dog>animal", RuntimeTypesDog().toString())  // dog>animal
        assertEquals("impl+hi-default", RuntimeTypesImpl().hi())  // impl+hi-default
        assertEquals("method-derived>method-base:call", RuntimeTypesSuperMethodDerived().inheritedMethod("call"))
        assertEquals("property-derived>property-base", RuntimeTypesSuperPropertyDerived().inheritedProperty)
        val b: RuntimeTypesBase = RuntimeTypesDerived()
        assertEquals("derived+base", b.greet())         // derived+base (virtual dispatch non-regression)
        assertEquals(11, b.twice(5))                    // 11
        val protected = RuntimeTypesProtectedSuperDerived()
        assertEquals("base", protected.inlineSuper())
        assertEquals("base", protected.liftedSuper()())
        with(RuntimeTypesSuperContext("base")) {
            assertEquals("base:value:marker",
                RuntimeTypesGenericProtectedSuperDerived<Int, String>()
                    .liftedSuper("value", RuntimeTypesStringMarker())())
        }
    }

    // #60: the star-projection smart-cast (`is Map<*,*>`/`is List<*>`/`is Iterable<*>`/`is Collection<*>`) on a
    // value-type-arg collection erased to `Any` must lower its castclass to the NON-generic BCL facade
    // (IDictionary/IList/ICollection/IEnumerable), NOT the object-erased generic interface — the CLR's reified
    // generics are INVARIANT, so a Dictionary<int,int> is NOT an IDictionary<object,object> and that cast throws
    // InvalidCastException. Proven here by the smart-casts SUCCEEDING and `.size`/`[i]` re-pointing onto the
    // non-generic ICollection.Count / IList.get_Item without throwing. The original case additionally asserted the
    // `println` RENDER ("{1=2, 3=4}" / "[10, 20, 30]") — that is the stdout-only clrElemToString path (a plain
    // `"$g"` / `toString()` on the erased value yields the raw .NET `Dictionary`2`/`List`1` ToString), NOT
    // reproducible as an in-process value, so these typed structural asserts (stronger for the cast subject)
    // stand in for it.
}

class StarProjectionAndVisibilityTests {
    @TestAttribute
    fun nonGenericFacade() {
        val g: Any = hashMapOf(1 to 2, 3 to 4)
        assertTrue(g is Map<*, *>)                       // smart-cast lowers to the non-generic IDictionary facade
        if (g is Map<*, *>) {
            assertEquals(2, g.size)                      // 2  (non-generic ICollection.Count)
        }
        val l: Any = listOf(10, 20, 30)
        assertTrue(l is List<*>)                         // -> non-generic IList facade
        if (l is List<*>) {
            assertEquals(3, l.size)                      // 3
            assertEquals(20, l[1])                       // 20 (non-generic IList.get_Item)
        }
        assertTrue(l is Iterable<*>)                     // -> non-generic IEnumerable facade
        assertTrue(l is Collection<*>)                  // -> composite Kotlin Collection classifier
        assertFalse((5 as Any) is Map<*, *>)            // False (a non-collection is not a Map)
        assertFalse(("x" as Any) is List<*>)            // False

        val stringFlow: RuntimeTypesExistentialFlow<String> = RuntimeTypesExistentialFlowImpl()
        val intFlow: RuntimeTypesExistentialFlow<Int> = RuntimeTypesExistentialFlowImpl()
        assertTrue(runtimeTypesFuse(stringFlow) === stringFlow)
        assertTrue(runtimeTypesFuse(intFlow) === intFlow)
    }

    @TestAttribute
    fun visibilityModifiers() {
        assertTrue(Type.GetType("RuntimeTypesPrivateInterface")!!.IsNotPublic)
        assertTrue(Type.GetType("RuntimeTypesInternalInterface")!!.IsNotPublic)
        assertTrue(Type.GetType("RuntimeTypesPublicInterface")!!.IsPublic)
        assertTrue(Type.GetType("RuntimeTypesPublishedInterface")!!.IsPublic)
        assertTrue(Type.GetType("RuntimeTypesPrivateAnnotation")!!.IsNotPublic)
        assertTrue(Type.GetType("RuntimeTypesInternalAnnotation")!!.IsNotPublic)
        assertTrue(Type.GetType("RuntimeTypesPublicAnnotation")!!.IsPublic)
        assertTrue(Type.GetType("RuntimeTypesPublishedAnnotation")!!.IsPublic)
        assertTrue(Type.GetType("RuntimeTypesPublishedClass")!!.IsPublic)
        assertTrue(Type.GetType("RuntimeTypesPublishedObject")!!.IsPublic)
        assertTrue(Type.GetType("RuntimeTypesPublishedEnum")!!.IsPublic)
        assertTrue(Type.GetType("RuntimeTypesPublishedRichEnum")!!.IsPublic)
        assertTrue(Type.GetType("RuntimeTypesVisibilityOwner+PrivateNestedInterface")!!.IsNestedPrivate)
        assertTrue(Type.GetType("RuntimeTypesVisibilityOwner+InternalNestedInterface")!!.IsNestedAssembly)
        assertTrue(Type.GetType("RuntimeTypesVisibilityOwner+ProtectedNestedInterface")!!.IsNestedFamily)
        assertTrue(Type.GetType("RuntimeTypesVisibilityOwner+PublicNestedInterface")!!.IsNestedPublic)
        assertTrue(Type.GetType("RuntimeTypesVisibilityOwner+PrivateNestedAnnotation")!!.IsNestedPrivate)
        assertTrue(Type.GetType("RuntimeTypesVisibilityOwner+InternalNestedAnnotation")!!.IsNestedAssembly)
        assertTrue(Type.GetType("RuntimeTypesVisibilityOwner+ProtectedNestedAnnotation")!!.IsNestedFamily)
        assertTrue(Type.GetType("RuntimeTypesVisibilityOwner+PublicNestedAnnotation")!!.IsNestedPublic)
        assertTrue(Type.GetType("RuntimeTypesAnnotationObjectOwner+InternalNestedAnnotation")!!.IsNestedAssembly)
        assertTrue(Type.GetType("RuntimeTypesAnnotationInterfaceOwner+PublicNestedAnnotation")!!.IsNestedPublic)
        assertTrue(Type.GetType("RuntimeTypesAnnotationObjectOwner+DepthOne+PrivateDeepAnnotation")!!.IsNestedPrivate)
        val annotationTarget = Type.GetType("RuntimeTypesNestedAnnotationTarget")!!
        assertTrue(annotationTarget.IsDefined(Type.GetType("RuntimeTypesVisibilityOwner+PublicNestedAnnotation")!!, false))
        assertTrue(annotationTarget.IsDefined(Type.GetType("RuntimeTypesAnnotationObjectOwner+InternalNestedAnnotation")!!, false))
        assertTrue(annotationTarget.IsDefined(Type.GetType("RuntimeTypesAnnotationInterfaceOwner+PublicNestedAnnotation")!!, false))
        val deepTarget = Type.GetType("RuntimeTypesAnnotationObjectOwner+DepthOne+AnnotatedDeepTarget")!!
        assertTrue(deepTarget.IsDefined(Type.GetType("RuntimeTypesAnnotationObjectOwner+DepthOne+PrivateDeepAnnotation")!!, false))
        val a = RuntimeTypesAccount(100)
        assertEquals(98, a.net())        // 98
        assertEquals("acct", a.tag())    // acct
        assertEquals(99, runtimeTypessecret())     // 99
        val byRefOwner = RuntimeTypesByRefOwner(41)
        runtimeTypesPrivateBackingAddress(byRefOwner)
        assertEquals(41, byRefOwner.slot)
    }

}

class TypeAliasTests {
    @TestAttribute
    fun acrossBoundary() {
        val ns: RuntimeTypesNames = runtimeTypesmakeNames()
        assertEquals("a,b,c", runtimeTypesjoin(ns))       // a,b,c
        assertEquals(3, ns.size)                 // 3
        val inc: RuntimeTypesIntOp = { it + 1 }
        assertEquals(12, runtimeTypesapply2(inc, 10))      // 12
        assertEquals(42, runtimeTypesunwrap(RuntimeTypesContainer(21))) // 42
        val p: RuntimeTypesPairs = mapOf("x" to 7, "y" to 9)
        assertEquals(9, runtimeTypeslookup(p, "y"))        // 9
        assertEquals(-1, runtimeTypeslookup(p, "z"))       // -1
    }

}

class CrossFileInterfaceAndPropertyTests {
    @TestAttribute
    fun crossFileIfaceDispatch() {
        crossFileLanguageCurrent = CrossFileLanguageImplementation()
        assertEquals(1, crossFileLanguageCall(1))               // 1 (dispatch reaches crossFileLanguage.CrossFileLanguageImplementation.go across file + namespace)
    }

    @TestAttribute
    fun crossFileTopLevelProp() {
        crossFileLanguageCounter = 0
        crossFileLanguageBump(); crossFileLanguageBump(); crossFileLanguageCounter = crossFileLanguageCounter + 5
        assertEquals(7, crossFileLanguageCounter)               // 7
    }
}
