// Round-trip consumer battery — consumes the producer's public API through the RE-IMPORTED dll (facadegen
// restored the Kotlin nature from DotKt.Metadata) and asserts each Kotlin-only shape survived the emit ->
// re-import round-trip. NONE of the producer's source is in this compilation — every `roundtrip.*` symbol
// resolves from the built RoundtripProducer.dll (the DLL-not-source invariant, design §3).
//
// First migrated batch (7 verify-roundtrip.sh sections -> 7 @TestAttribute methods, golden values preserved
// 1:1 as `// <expected>` trailing comments, per design D1 value asserts):
//   enumInheritedMembers        <- roundtrip-enum            (#105)
//   customAccessorProperties    <- roundtrip-customprop      (#103)
//   defaultAndNamedArgs         <- roundtrip-defargs         (#134)
//   triStateNullability         <- roundtrip-nrt             (#48)
//   memberExtensionFunctions    <- roundtrip-memext
//   operatorAndInfixFromRealFlag<- roundtrip-operator-flag   (#146)
//   genericOperatorGetSet       <- roundtrip-generic-operator(#133)
// (roundtrip-toplevel-val stays in the shell lane: a top-level PLAIN-field file class is not surfaced by
//  facadegen's --import-list path when reached only through field imports — a facadegen re-import gap.)
import roundtrip.palette.Color
import roundtrip.cprop.topProp
import roundtrip.cprop.topVar
import roundtrip.cprop.topGetVar
import roundtrip.cprop.Host
import roundtrip.defargs.greet
import roundtrip.defargs.box
import roundtrip.defargs.flags
import roundtrip.defargs.kinds
import roundtrip.defargs.Pt
import roundtrip.nrt.retNonNull
import roundtrip.nrt.takeNonNull
import roundtrip.nrt.retNullable
import roundtrip.nrt.takeNullable
import roundtrip.nrt.retNullableInt
import roundtrip.memext.Box
import roundtrip.memext.Lib
import roundtrip.money.Money
import roundtrip.genop.Arr
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert

class RoundtripTests {
    // roundtrip-enum (#105): a basic enum's INHERITED System.Enum members (toString/==/hashCode) round-trip.
    @TestAttribute
    fun enumInheritedMembers() {
        ClassicAssert.AreEqual("RED", Color.RED.toString())   // RED    inherited System.Enum.ToString on a value-type receiver
        ClassicAssert.IsFalse(Color.RED == Color.GREEN)       // False  structural inequality
        ClassicAssert.AreEqual(0, Color.RED.hashCode())       // 0      inherited System.Enum.GetHashCode (RED underlying int = 0)
    }

    // roundtrip-customprop (#103): field-backed property with a CUSTOM accessor invokes the getter/setter
    // (not the raw field) cross-module — top-level + companion + member, independent get/set customness.
    @TestAttribute
    fun customAccessorProperties() {
        ClassicAssert.AreEqual(42, topProp)                   // 42  custom getter, not raw 41
        val h = Host()
        ClassicAssert.AreEqual(107, h.kProp)                  // 107
        ClassicAssert.AreEqual(20, Host.cProp)                // 20  companion custom getter
        topVar = 10
        ClassicAssert.AreEqual(15, topVar)                    // 15  custom setter
        h.kVar = 3
        ClassicAssert.AreEqual(6, h.kVar)                     // 6   member custom setter
        topGetVar = 50
        ClassicAssert.AreEqual(49, topGetVar)                 // 49  custom getter, default setter
    }

    // roundtrip-defargs (#134): default args omitted trailing / named-middle / reordered, on fns + ctors.
    @TestAttribute
    fun defaultAndNamedArgs() {
        ClassicAssert.AreEqual("Hi, A!", greet("A"))                        // Hi, A!
        ClassicAssert.AreEqual("Yo, B!", greet("B", "Yo"))                  // Yo, B!   trailing omit
        ClassicAssert.AreEqual("Hi, C?", greet("C", punct = "?"))          // Hi, C?   NAMED MIDDLE omission
        ClassicAssert.AreEqual("Hey, E!", greet(greeting = "Hey", name = "E")) // Hey, E!  reordered named
        ClassicAssert.AreEqual(123, box(1))                                 // 123
        ClassicAssert.AreEqual(129, box(1, c = 9))                          // 129      NAMED MIDDLE omission
        ClassicAssert.AreEqual(527, box(a = 5, c = 7))                      // 527      named middle omission
        ClassicAssert.AreEqual("True/x y", flags())                        // True/x y  string default with a space
        ClassicAssert.AreEqual("True/z", flags(label = "z"))               // True/z    named middle omission
        ClassicAssert.AreEqual("a/5/1.5/z/none", kinds("a"))               // a/5/1.5/z/none   all defaults (Long/Double/Char/null)
        ClassicAssert.AreEqual("b/5/1.5/q/none", kinds("b", ch = 'q'))     // b/5/1.5/q/none   NAMED MIDDLE omit skipping Long+Double
        ClassicAssert.AreEqual("c/5/1.5/z/hi", kinds("c", note = "hi"))    // c/5/1.5/z/hi     NAMED-MIDDLE omit filling the null slot
        ClassicAssert.AreEqual("(0,4)", Pt(y = 4).toString())              // (0,4)    ctor named middle omission
        ClassicAssert.AreEqual("(7,0)", Pt(x = 7).toString())              // (7,0)    ctor named
    }

    // roundtrip-nrt (#48): tri-state nullability — non-null (byte 1) + nullable (byte 2) reference via
    // compile-dependency, + value Nullable<int> structural.
    @TestAttribute
    fun triStateNullability() {
        ClassicAssert.AreEqual(1, retNonNull().length)              // 1   NO ?. — compiles only if the return restored non-null
        ClassicAssert.AreEqual(4, takeNonNull("abcd"))             // 4   non-null param called with a non-null
        ClassicAssert.AreEqual(-1, retNullable(false)?.length ?: -1) // -1 nullable return, null branch
        ClassicAssert.AreEqual(1, retNullable(true)?.length ?: -1)  // 1   nullable return, value branch
        ClassicAssert.AreEqual(-1, takeNullable(null))             // -1  passing null compiles only if the param restored nullable
        ClassicAssert.AreEqual(5, takeNullable("hello"))           // 5   nullable param with a non-null arg
        ClassicAssert.AreEqual(-1, retNullableInt(false) ?: -1)    // -1  value Nullable<int> — the null (HasValue=false) branch
        ClassicAssert.AreEqual(1, retNullableInt(true) ?: -1)      // 1   value Nullable<int> — the value branch
    }

    // roundtrip-memext: member extension functions (plain/infix/operator/inline-generic/protected) via with().
    @TestAttribute
    fun memberExtensionFunctions() {
        val lib = Lib(10)
        with(lib) {
            ClassicAssert.AreEqual(15, Box(5).boost())            // 15
            ClassicAssert.AreEqual(15, Box(2) glue Box(3))       // 15
            ClassicAssert.AreEqual(22, Box(4) * 3)               // 22
            ClassicAssert.AreEqual(8, Box(7).mapped { it + 1 })  // 8
            ClassicAssert.AreEqual(18, Box(7).boostedBy { it + 1 })  // (7+1)+10 = 18  #23 dual-receiver inline member-extension (reads extension get() AND dispatch k)
        }
        ClassicAssert.AreEqual(110, lib.useProt(Box(1)))         // 110
    }

    // roundtrip-operator-flag (#146): operator compareTo (via </>) + infix restored from the REAL flag.
    @TestAttribute
    fun operatorAndInfixFromRealFlag() {
        ClassicAssert.IsTrue(Money(5) < Money(9))                 // True   compareTo restored from the REAL operator flag
        ClassicAssert.IsTrue(Money(9) > Money(5))                 // True
        ClassicAssert.AreEqual(5, (Money(2) combine Money(3)).cents) // 5   infix restored
    }

    // roundtrip-generic-operator (#133): a Kotlin operator get/set on a generic DotKt type resolves cross-module.
    @TestAttribute
    fun genericOperatorGetSet() {
        val r = Arr(arrayOf("a", "b"))
        ClassicAssert.AreEqual("b", r[1])                        // b   generic operator get
        val r2 = Arr(arrayOf(10, 20))
        r2[0] = 99                                               // generic operator set
        ClassicAssert.AreEqual(99, r2[0])                        // 99
    }
}
