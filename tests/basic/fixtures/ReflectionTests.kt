// Reflection / KClass-name battery (#138) — `T::class.simpleName` / `.qualifiedName` must report the KOTLIN name (the
// KDoc contract "the name as declared in source code"), NOT the .NET reflection name. bir2cir's KClassMemberBinding
// const-folds the accessor to the Kotlin name whenever the receiver's Kotlin type is statically known: an UNBOUND
// `Int::class`/`ReflBox::class` (a `classRef`), or a BOUND `1::class`/`"x"::class` on a known-final builtin (a `getType`
// whose argument's static type is final, so the runtime class == the static type). Each method asserts the exact Kotlin
// contract value (`"Int"` / `"kotlin.Int"`), which the pre-fix code got wrong (`"Int32"` / `"System.Int32"`).
//
// Top-level class names are unique within this single battery assembly (Refl* prefix); every method body is
// self-contained. The genuinely-dynamic `x::class` (open/interface static type) is NOT covered here — its run-time
// CLR->Kotlin reversal is a sequenced stdlib follow-up (docs/dotkt-semantics.md §5g).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

class ReflWidget {
    class Inner
}

class ReflBox<T>(val v: T)

class ReflectionTests {
    // Bound `value::class` on a known-final builtin (getType-fold): Int, primitive tower, String. The reported #138
    // failures (`1::class.simpleName == "Int32"`, `.qualifiedName == "System.Int32"`).
    @TestAttribute
    fun boundPrimitiveValuesReportKotlinNames() {
        assertEquals("Int", 1::class.simpleName)              // was "Int32"
        assertEquals("kotlin.Int", 1::class.qualifiedName)    // was "System.Int32"
        assertEquals("Long", 1L::class.simpleName)
        assertEquals("kotlin.Long", 1L::class.qualifiedName)
        assertEquals("Double", 3.14::class.simpleName)
        assertEquals("kotlin.Double", 3.14::class.qualifiedName)
        assertEquals("Float", 2.0f::class.simpleName)
        assertEquals("kotlin.Float", 2.0f::class.qualifiedName)
        assertEquals("Boolean", true::class.simpleName)
        assertEquals("kotlin.Boolean", true::class.qualifiedName)
        assertEquals("Char", 'c'::class.simpleName)
        assertEquals("kotlin.Char", 'c'::class.qualifiedName)
    }

    // Bound `String` value::class — both a literal receiver and a local of the final String type.
    @TestAttribute
    fun boundStringValuesReportKotlinNames() {
        assertEquals("String", "x"::class.simpleName)
        assertEquals("kotlin.String", "x"::class.qualifiedName)  // was "System.String"
        val s = "hello"
        assertEquals("String", s::class.simpleName)              // local of a final type folds too
        assertEquals("kotlin.String", s::class.qualifiedName)
    }

    // Unbound `Type::class` (classRef-fold): the literal type identity, always resolvable — primitive tower + String.
    @TestAttribute
    fun classLiteralsReportKotlinNames() {
        assertEquals("Int", Int::class.simpleName)
        assertEquals("kotlin.Int", Int::class.qualifiedName)
        assertEquals("String", String::class.simpleName)
        assertEquals("kotlin.String", String::class.qualifiedName)
        assertEquals("Short", Short::class.simpleName)
        assertEquals("kotlin.Short", Short::class.qualifiedName)
        assertEquals("Byte", Byte::class.simpleName)
        assertEquals("kotlin.Byte", Byte::class.qualifiedName)
    }

    // A generic class literal drops its type args -> the raw type name, never a backtick-mangled `IList``1`.
    @TestAttribute
    fun reportsRawName() {
        assertEquals("ReflBox", ReflBox::class.simpleName)
        assertEquals("ReflBox", ReflBox::class.qualifiedName)   // default package -> qualified == simple
    }

    // A user class (unbound) and its NESTED class: simpleName is the last segment, qualifiedName the dotted path.
    @TestAttribute
    fun userAndNestedClassesReportKotlinNames() {
        assertEquals("ReflWidget", ReflWidget::class.simpleName)
        assertEquals("ReflWidget", ReflWidget::class.qualifiedName)
        assertEquals("Inner", ReflWidget.Inner::class.simpleName)
        assertEquals("ReflWidget.Inner", ReflWidget.Inner::class.qualifiedName)
    }

    // Specialized primitive arrays are final builtins that CLR-rename to `int[]`/… — both unbound `IntArray::class` and a
    // bound array local fold to the Kotlin name (not `"Int32[]"` / `"System.Int32[]"`).
    @TestAttribute
    fun reportKotlinName() {
        assertEquals("IntArray", IntArray::class.simpleName)
        assertEquals("kotlin.IntArray", IntArray::class.qualifiedName)
        val ia = intArrayOf(1, 2, 3)
        assertEquals("IntArray", ia::class.simpleName)              // bound local of a final array type folds
        assertEquals("kotlin.IntArray", ia::class.qualifiedName)
        val ca = charArrayOf('a', 'b')
        assertEquals("CharArray", ca::class.simpleName)
        assertEquals("kotlin.CharArray", ca::class.qualifiedName)
    }
}
