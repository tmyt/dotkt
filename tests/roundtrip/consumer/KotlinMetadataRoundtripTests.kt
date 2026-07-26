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
//
// Second migrated batch (8 more sections -> 8 @TestAttribute methods):
//   nothingReturnGeneric        <- roundtrip-nothing-return  (#133)  Nothing in a generic top-level fn + if/else
//   packagedNamespaces          <- roundtrip-pkg                    namespaces / reified inline / non-local return / ext op+prop / vararg / default / nullable
//   inlineMemberNonLocalReturn  <- roundtrip-inline-member   (#60)   cross-module inline MEMBER + non-local return + dispatch-receiver field read
//   genericInlineExtension      <- roundtrip-generic-inline-ext(#133) generic inline ext on a generic receiver infers T
//   dottedFileClass             <- roundtrip-dotfile         (#16)   top-level fun in a dotted-name file class resolves
//   nonConstDefaultArgs         <- roundtrip-nonconst-default(#146)  = {} / = emptyList() filled cross-module
//   comparableClass             <- roundtrip-comparable      (#179)  class C : Comparable<C> </>/<=/>=/sorted()
//   ubyteFidelity               <- roundtrip-ubyte                  UByte/UByteArray strict-mapping fidelity
//   toplevelValVar              <- roundtrip-toplevel-val   (#195)  bare top-level val/var -> plain static FIELD (no accessor) resolved cross-module via facadegen --import-list
// STAYED in the shell lane (tests/roundtrip/scenarios/run.sh):
//   roundtrip-nothing         — a cross-module Nothing branch merges an `object`-returning call with `string`
//                               (StackUnexpected object/string; else-branch throws so RUN is green). Tracked as #197.
//   roundtrip-generic-hof / roundtrip-receiver-lambda — now formally clean after low-arity delegate ABI unification;
//                               pending only mechanical migration to this in-process lane.
// (roundtrip-comparable-meta stays in shell: it asserts on the generated facadegen metadata JSON directly.)
@file:OptIn(kotlin.ExperimentalUnsignedTypes::class)
import roundtrip.palette.Color
import kotlinx.roundtrip.palette.StartMode
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
import roundtrip.nrt.NullableCtorHolder
import roundtrip.nrt.NullableValueCtor
import roundtrip.memext.Box
import roundtrip.memext.Lib
import roundtrip.money.Money
import roundtrip.genop.Arr
import roundtrip.nothingret.pick
import roundtrip.nothingret.fail as nothingretFail
import roundtrip.pkg.Vec
import roundtrip.pkg.Dir
import roundtrip.pkg.greet as pkgGreet
import roundtrip.pkg.typeName
import roundtrip.pkg.forEach3
import roundtrip.pkg.plus
import roundtrip.pkg.manhattan
import roundtrip.pkg.sumAll
import roundtrip.pkg.tagged as pkgTagged
import roundtrip.pkg.orNone
import roundtrip.picker.C
import roundtrip.gie.Cell
import roundtrip.gie.update
import roundtrip.dotfile.commonOnly
import roundtrip.dotfile.Box as DfBox
import roundtrip.nc.Panel as NcPanel
import roundtrip.nc.column as ncColumn
import roundtrip.nc.run2
import roundtrip.nc.tagged as ncTagged
import roundtrip.cmp.Ver
import roundtrip.ubyte.ub
import roundtrip.ubyte.uba
import roundtrip.ubyte.takeUb
import roundtrip.tlval.greeting
import roundtrip.tlval.counter
import roundtrip.tlval.origin
import roundtrip.inldelegate.applyViaNestedDelegate
import roundtrip.inldelegate.applyViaGenericNestedDelegate
import roundtrip.inldelegate.applyViaTransitivelyNestedDelegate
import roundtrip.inldelegate.NestedDelegateHost
// #199-①: two same-simple-name GENERIC types (`Cell<T>`) in different producer packages, aliased here.
import roundtrip.genclash.a.Cell as CellA
import roundtrip.genclash.b.Cell as CellB
import roundtrip.genclash.a.cellA
import roundtrip.genclash.b.cellB
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert

class KotlinApiShapeRoundtripTests {
    // #43: the producer inline payload contains a newDelegate whose lifted method belongs
    // to the producer file. Cross-module splicing must carry and re-home that implementation.
    @TestAttribute
    fun crossModuleInlineNestedDelegate() {
        ClassicAssert.AreEqual(7, applyViaNestedDelegate(3) { it + 1 })
        ClassicAssert.AreEqual("ok!", applyViaGenericNestedDelegate("ok") { it + "!" })
        ClassicAssert.AreEqual(10, applyViaTransitivelyNestedDelegate(3) { it + 1 })
        ClassicAssert.AreEqual(5, NestedDelegateHost().apply(3) { it + 1 })
        ClassicAssert.AreEqual(42, nestedDelegateNonLocalReturn())
    }

    private fun nestedDelegateNonLocalReturn(): Int {
        applyViaNestedDelegate(3) { if (it == 6) return 42 else it }
        return -1
    }

    // roundtrip-enum (#105): a basic enum's INHERITED System.Enum members (toString/==/hashCode) round-trip.
    @TestAttribute
    fun enumInheritedMembers() {
        ClassicAssert.AreEqual("RED", Color.RED.toString())   // RED    inherited System.Enum.ToString on a value-type receiver
        ClassicAssert.IsFalse(Color.RED == Color.GREEN)       // False  structural inequality
        ClassicAssert.AreEqual(0, Color.RED.hashCode())       // 0      inherited System.Enum.GetHashCode (RED underlying int = 0)
        ClassicAssert.AreEqual(42, StartMode.DEFAULT.marker()) // class-like enum entry: injected static owner survives re-import
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

    // #251: CONSTRUCTOR-parameter nullability. Every `null` below is the sharp signal — it compiles only if the
    // ctor param re-imported as `String?`; a param restored non-null fails with "null cannot be a value of a
    // non-null type 'String'". Covers a primary, a secondary and a value-typed (`Int?`) ctor param.
    @TestAttribute
    fun nullableConstructorParams() {
        ClassicAssert.AreEqual(-1, NullableCtorHolder(null).len())          // -1  PRIMARY ctor param restored nullable
        ClassicAssert.AreEqual(2, NullableCtorHolder("ab").len())           // 2   primary ctor with a non-null arg
        ClassicAssert.AreEqual(-1, NullableCtorHolder(2, null).len())       // -1  SECONDARY ctor param restored nullable
        ClassicAssert.AreEqual(4, NullableCtorHolder(2, "ab").len())        // 4   secondary ctor: "ab".repeat(2)
        ClassicAssert.AreEqual(-2, NullableValueCtor(null, null).sum())     // -2  value Nullable<int> + reference, both null
        ClassicAssert.AreEqual(6, NullableValueCtor(3, "abc").sum())        // 6   3 + 3
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

    // ---- second batch --------------------------------------------------------------------------------

    // roundtrip-nothing-return (#133): a Nothing return round-trips through a generic fn + a bare if/else.
    @TestAttribute
    fun nothingReturnGeneric() {
        ClassicAssert.AreEqual(7, pick(true, 7))                 // 7   generic pick, Nothing else-branch
        val y: String = if (true) "ok" else nothingretFail("x")             // String only if fail(): Nothing round-tripped
        ClassicAssert.AreEqual("ok", y)                          // ok
    }

    // roundtrip-pkg: namespaces; reified inline -> generic method; cross-module inline + non-local return;
    // properties (custom getter + mutable write); top-level ext operator + ext property; vararg; default; nullable.
}

class PackageAndInlineRoundtripTests {
    @TestAttribute
    fun packagedNamespaces() {
        ClassicAssert.AreEqual(11, Vec(1, 2) dot Vec(3, 4))      // 11   geom.Vec, infix
        ClassicAssert.AreEqual("Hi, pkg", pkgGreet("pkg"))       // Hi, pkg   top-level via import
        ClassicAssert.AreEqual("EAST", Dir.EAST.toString())      // EAST  enum in a package
        ClassicAssert.AreEqual("String", typeName<String>())     // String  cross-module reified inline -> generic method call
        ClassicAssert.AreEqual(4, firstEven())                   // 4     cross-module inline + lambda + non-local return
        val v = Vec(3, 4)
        ClassicAssert.AreEqual(25, v.mag2)                       // 25    property (custom getter)
        v.x = 6
        ClassicAssert.AreEqual(52, v.mag2)                       // 52    mutable property write
        ClassicAssert.AreEqual(52, (Vec(1, 2) + Vec(3, 4)).mag2) // 52    top-level extension operator + property
        ClassicAssert.AreEqual(10, sumAll(1, 2, 3, 4))           // 10    vararg
        ClassicAssert.AreEqual(7, Vec(3, 4).manhattan)           // 7     extension property
        ClassicAssert.AreEqual("def", pkgTagged())               // def   default argument omitted
        ClassicAssert.AreEqual("none", orNone(null))             // none  nullable param (null passable)
    }
    private fun firstEven(): Int {
        forEach3(1, 3, 4) { if (it % 2 == 0) return it }         // NON-LOCAL return through a CROSS-MODULE inline lambda
        return -1
    }

    // roundtrip-inline-member (#60): cross-module inline MEMBER + non-local return from the CALLER + a
    // dispatch-receiver field read in the spliced body.
    @TestAttribute
    fun inlineMemberNonLocalReturn() {
        ClassicAssert.AreEqual(99, caller())     // 99   the non-local return escapes the CALLER, not the delegate
        ClassicAssert.AreEqual(30, matched())    // 30   inline-member body early-return + `this.c` field read
    }
    private fun caller(): Int {
        val c = C(10, 20, 30)
        c.pick { x -> if (x == 20) return 99; false }   // NON-LOCAL return from caller() through the CROSS-MODULE inline MEMBER
        return -1                                        // must NOT be reached
    }
    private fun matched(): Int {
        val c = C(10, 20, 30)
        return c.pick { x -> x == 30 }                   // pick's own early `return c` (dispatch-receiver read) yields 30
    }

    // roundtrip-generic-inline-ext (#133): a generic inline extension on a generic receiver infers T from the receiver.
    @TestAttribute
    fun genericInlineExtension() {
        val c = Cell(1)
        c.update { it + 1 }                              // infer T=Int from the receiver Cell<T>
        ClassicAssert.AreEqual(2, c.v)                   // 2
    }

    // roundtrip-dotfile (#16): a top-level fun in a dotted-name file class (Dotfile.common.kt -> Dotfile_commonKt)
    // resolves cross-module; the top-level class in the same file round-trips either way.
    @TestAttribute
    fun dottedFileClass() {
        ClassicAssert.AreEqual(2, commonOnly(1))         // 2   top-level fun from the dotted-name file class
        ClassicAssert.AreEqual(2, DfBox(2).v)            // 2   top-level class from the same file
    }

    // roundtrip-nonconst-default (#146): a NON-CONST default (`= {}` empty receiver/plain lambda, `= emptyList()`
    // simple-expr) filled cross-module.
    @TestAttribute
    fun nonConstDefaultArgs() {
        ClassicAssert.AreEqual(2, ncColumn(build = { add("hi") }))                     // 2   configure defaults to {} (empty receiver lambda)
        ClassicAssert.AreEqual(3, ncColumn(configure = { add("ab") }, build = { add("c") })) // 3  both provided (no fill)
        ClassicAssert.AreEqual("ok", run2(body = { }))                                 // ok  pre defaults to {} (empty plain lambda)
        ClassicAssert.AreEqual("z=0", ncTagged("z"))                                   // z=0 items defaults to emptyList()
    }

    // roundtrip-comparable (#179): a `class C : Comparable<C>` — </>/<=/>= + sorted() resolve+run cross-module
    // (facadegen restores operator compareTo + the kotlin.Comparable supertype; bir2cir binds compareTo->CompareTo).
}

class ComparableUnsignedAndPropertyRoundtripTests {
    @TestAttribute
    fun comparableClass() {
        ClassicAssert.IsTrue(Ver(3) < Ver(5))                   // True   `<`  -> restored operator compareTo
        ClassicAssert.IsTrue(Ver(9) > Ver(2))                   // True   `>`
        ClassicAssert.IsTrue(Ver(4) <= Ver(4))                  // True   `<=`
        ClassicAssert.IsFalse(Ver(7) >= Ver(8))                 // False  `>=`
        val xs = listOf(Ver(3), Ver(1), Ver(2)).sorted()        // sorted() needs Ver : Comparable<Ver> (supertype restored)
        ClassicAssert.AreEqual(1, xs[0].n)                      // 1   smallest first
        ClassicAssert.AreEqual(3, xs[2].n)                      // 3   largest last
    }

    // roundtrip-ubyte: UByte/UByteArray strict-mapping fidelity — a mis-restored signed Byte would print -56 for 200.
    @TestAttribute
    fun ubyteFidelity() {
        val u: UByte = ub()                                     // compiles ONLY if the return restored UByte (not Byte)
        ClassicAssert.AreEqual(200, u.toInt())                  // 200  unsigned fidelity
        val a: UByteArray = uba()                               // compiles ONLY if byte[] restored to UByteArray
        ClassicAssert.AreEqual(3, a.size)                       // 3
        ClassicAssert.AreEqual(250, a[2].toInt())               // 250
        ClassicAssert.AreEqual(200, takeUb(200u))               // 200  pass a UByte to a UByte-restored param
    }

    // roundtrip-toplevel-val (#195): a bare top-level `val`/`var` with NO custom accessor compiles to a plain
    // static FIELD (no get_/set_), reachable ONLY through the field. The consumer reads the library's top-level
    // property DIRECTLY (`import roundtrip.tlval.greeting`), NOT via a re-exposing function — proving facadegen's
    // --import-list now surfaces the field-backed val/var from the BUILT dll (the #195 facadegen gap). Covers a
    // `val: String`, a `var: Int` (read + cross-module write `+=`), and a `val` of a USER type.
    @TestAttribute
    fun toplevelValVar() {
        ClassicAssert.AreEqual("hi", greeting)                  // hi   top-level val -> plain static field, read directly
        counter += 2                                            // cross-module write to a top-level var
        ClassicAssert.AreEqual(42, counter)                     // 42   read back the written value (40 + 2)
        ClassicAssert.AreEqual("(1, 2)", origin.toString())     // (1, 2)  top-level val of a USER type
    }

    // #199-①: two GENERIC types sharing the simple name `Cell` in DIFFERENT producer packages must stay DISTINCT on
    // re-import. Each factory's declared return type is annotated with the package-qualified `Cell` alias, so if
    // facadegen dropped the namespace (bare `Cell`, last-wins) the return would resolve to the WRONG package's type
    // and this assignment would not compile. The `var value` proves mutability survived; `.boxed()`/`.value` prove
    // members resolve on the correctly-qualified type.
}

class GenericNameCollisionRoundtripTests {
    @TestAttribute
    fun genericSameSimpleNameAcrossPackages() {
        val a: CellA<Int> = cellA(5)                 // return type is roundtrip.genclash.a.Cell, not b.Cell
        a.value = 6                                  // `var` survived the round-trip (would fail if degraded to val)
        ClassicAssert.AreEqual(6, a.value)           // 6
        ClassicAssert.AreEqual(6, a.boxed())         // 6   member resolves on a.Cell
        val b: CellB<String> = cellB("x")            // return type is roundtrip.genclash.b.Cell, not a.Cell
        b.value = "y"                                // b.Cell's `var` survived independently
        ClassicAssert.AreEqual("y", b.value)         // y
        ClassicAssert.AreEqual("y", b.boxed())       // y   member resolves on b.Cell
    }
}
