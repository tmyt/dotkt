// C#-producer roundtrip consumer battery B — .NET ATTRIBUTE interop (an existing System.Attribute-derived .NET
// attribute surfaced as a Kotlin annotation and applied on Kotlin declarations; the backend re-applies the real
// .NET attribute via SetCustomAttribute). Each case's golden RUN values asserted 1:1 (the reflection checks in
// the original samples are compile-time facts; the runtime values are what RUN produced).
//   netattr        <- il-netattr         a .NET attribute with (string,int) ctor, applied on class + fun (#54)
//   netattr-vararg <- il-netattr-vararg  a .NET attribute whose ONLY ctor is `params object[]`, applied bare (#184)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
// il-netattr
import NetAttr.LabelAttribute
// il-netattr-vararg
import NetAttrVararg.TagAttribute

// il-netattr top-level decls (prefixed uniquely so they cannot collide with netattr-vararg's Widget/helper below).
@LabelAttribute("entity", 5)
class NetAttrWidget(val id: Int) { fun show() = "widget#$id" }

@LabelAttribute("helper", 1)
fun netAttrHelper(n: Int) = n * 2

// il-netattr-vararg top-level decls: the params-object[]-only attribute applied BARE (zero args) and with args.
@TagAttribute
class NetAttrVarargWidget(val id: Int) { fun show() = "widget#$id" }

@TagAttribute("helper", 1)
fun netAttrVarargHelper(n: Int) = n * 2

class AttributeTests {
    @TestAttribute
    fun netattr() {
        assertEquals("widget#7", NetAttrWidget(7).show())  // widget#7
        assertEquals(42, netAttrHelper(21))                // 42
    }

    @TestAttribute
    fun netattrVararg() {
        assertEquals("widget#7", NetAttrVarargWidget(7).show())  // widget#7
        assertEquals(42, netAttrVarargHelper(21))               // 42
    }
}
