// Nullable / value-type-nullability battery — migrates cases/il-smartcast and il-arrnull.
// Asserting the VALUE (including IsNull for the null slots) is strictly stronger than the old
// println("null") stdout diff: a wrong non-null would print its toString and could alias; IsNull cannot.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert

// il-smartcast: `as?` safe cast — value type (-> T?) and reference type (-> isinst).
fun describe(x: Any): String {
    val n = x as? Int
    return if (n != null) "int:$n" else "other"
}
fun asStr(x: Any): String {
    val s = x as? String
    return s ?: "none"
}

class NullableTests {
    @TestAttribute
    fun safeCast() {
        ClassicAssert.AreEqual("int:42", describe(42))
        ClassicAssert.AreEqual("other", describe("hi"))
        ClassicAssert.AreEqual("yo", asStr("yo"))
        ClassicAssert.AreEqual("none", asStr(7))
    }

    // il-arrnull (#113): arrayOfNulls<T>(n) for a value-type T allocates Nullable<T>[] (value-type
    // nullability preserved), and copyOf() is an independent round-trip. General across Int/Long/Double/Char/String.
    @TestAttribute
    fun arrayOfNullsValueTypes() {
        val a = arrayOfNulls<Int>(3)
        a[0] = 5
        ClassicAssert.AreEqual(5, a[0])
        ClassicAssert.IsNull(a[1])
        ClassicAssert.AreEqual(3, a.size)

        // copyOf() -> nativeClone() as Array<T?> round-trip: only succeeds when `a` is a real Nullable<int>[].
        val c = a.copyOf()
        c[1] = 7
        ClassicAssert.AreEqual(5, c[0])
        ClassicAssert.AreEqual(7, c[1])
        ClassicAssert.IsNull(a[1])              // copy is independent
        ClassicAssert.AreEqual("[5, null, null]", a.toList().toString())
    }

    @TestAttribute
    fun arrayOfNullsAcrossPrimitives() {
        val la = arrayOfNulls<Long>(2)
        la[0] = 100L
        ClassicAssert.AreEqual(100L, la[0])
        ClassicAssert.IsNull(la[1])

        val da = arrayOfNulls<Double>(2)
        da[1] = 2.5
        ClassicAssert.IsNull(da[0])
        ClassicAssert.AreEqual(2.5, da[1])

        val ca = arrayOfNulls<Char>(2)
        ca[0] = 'x'
        ClassicAssert.AreEqual('x', ca[0])
        ClassicAssert.IsNull(ca[1])

        // Reference-type arg stays correct (Nullable-wrap is a no-op for reference elements).
        val sa = arrayOfNulls<String>(2)
        sa[0] = "hi"
        ClassicAssert.AreEqual("hi", sa[0])
        ClassicAssert.IsNull(sa[1])
    }
}
