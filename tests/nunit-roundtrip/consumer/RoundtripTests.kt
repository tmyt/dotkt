// Consumes the producer's public API through the RE-IMPORTED dll (facadegen restored the Kotlin nature from
// DotKt.Metadata) and asserts each Kotlin-only shape survived the round-trip: top-level fn/prop, overloads,
// default args, extension, inline, operator/infix, interface default + inheritance + virtual dispatch,
// generics, nullable reference/return, and a cross-module suspend call. NONE of the producer's source is in
// this compilation — every `roundtrip.api.*` symbol resolves from the built RoundtripProducer.dll.
import roundtrip.api.libraryName
import roundtrip.api.topLevelGreeting
import roundtrip.api.combine
import roundtrip.api.withDefaults
import roundtrip.api.echoTwice
import roundtrip.api.applyTwice
import roundtrip.api.Money
import roundtrip.api.Shape
import roundtrip.api.Rect
import roundtrip.api.Square
import roundtrip.api.Wrap
import roundtrip.api.firstOrNull2
import roundtrip.api.lengthOr
import roundtrip.api.asyncDouble
import dotkt.support.blockOn
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert

class RoundtripTests {
    @TestAttribute
    fun topLevelFunctionAndProperty() {
        ClassicAssert.AreEqual("roundtrip-producer", libraryName)
        ClassicAssert.AreEqual("hello, world", topLevelGreeting("world"))
    }

    @TestAttribute
    fun overloadsResolveAcrossTheDll() {
        ClassicAssert.AreEqual(7, combine(3, 4))
        ClassicAssert.AreEqual("ab", combine("a", "b"))
    }

    @TestAttribute
    fun defaultArgsSurvive() {
        ClassicAssert.AreEqual(15, withDefaults(5))
        ClassicAssert.AreEqual(8, withDefaults(5, 3))
    }

    @TestAttribute
    fun extensionFunctionReimported() {
        ClassicAssert.AreEqual("hihi", "hi".echoTwice())
    }

    @TestAttribute
    fun inlineFunctionReimported() {
        ClassicAssert.AreEqual(4, applyTwice(0) { it + 2 })
    }

    @TestAttribute
    fun operatorAndInfix() {
        val total = Money(100) + Money(50)
        ClassicAssert.AreEqual(150, total.cents)
        ClassicAssert.AreEqual(300, (Money(100) scaledBy 3).cents)
    }

    @TestAttribute
    fun interfaceInheritanceVirtualDispatch() {
        val s: Shape = Square(4)
        ClassicAssert.AreEqual(16, s.area())                   // abstract override dispatched through 2 levels
        ClassicAssert.AreEqual("rect area=16", s.describe())   // Square inherits Rect.describe; area() -> Square's
        val r: Shape = Rect(2, 3)
        ClassicAssert.AreEqual("rect area=6", r.describe())    // direct child override of interface default
    }

    @TestAttribute
    fun genericsAndNullableReturn() {
        ClassicAssert.AreEqual(42, Wrap(42).unwrap())
        ClassicAssert.AreEqual("x", Wrap("x").unwrap())
        ClassicAssert.AreEqual(1, firstOrNull2(listOf(1, 2)))
        ClassicAssert.IsNull(firstOrNull2(listOf<Int>()))
    }

    @TestAttribute
    fun nullableReferenceParam() {
        ClassicAssert.AreEqual(3, lengthOr("abc", -1))
        ClassicAssert.AreEqual(-1, lengthOr(null, -1))
    }

    @TestAttribute
    fun suspendBridge() {
        ClassicAssert.AreEqual(10, blockOn { asyncDouble(5) })
    }
}
