// Round-trip consumer battery — consumes the producer's public API through the RE-IMPORTED dll (dll2klib
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
//   nonConstDefaultArgs         <- roundtrip-nonconst-default(#146)  = {} / = emptyList() filled cross-module,
//                                                             plus (#235) a CONSTRUCTOR's non-const default
//   nonConstDefaultArgsEvaluateOnce                           (#235) a spliced receiver/argument runs exactly once
//   comparableClass             <- roundtrip-comparable      (#179)  class C : Comparable<C> </>/<=/>=/sorted()
//   ubyteFidelity               <- roundtrip-ubyte                  UByte/UByteArray strict-mapping fidelity
//   toplevelValVar              <- roundtrip-toplevel-val   (#195)  bare top-level val/var -> plain static FIELD (no accessor) resolved through the reference KLIB
//   crossModuleContextParameters                             context parameters restored AS context parameters from
//                                                             [KotlinContextParameter] (functions, defaults, properties)
//   crossModuleNothingBranchMerge <- roundtrip-nothing       (#135/#197) companion-static + top-level `fun f(): Nothing`
//                                                             in a value merge; the merge is now well-typed IL, which
//                                                             is what let this section leave the stdout-only shell lane
// STAYED in the shell lane (tests/roundtrip/scenarios/run.sh):
//   roundtrip-generic-hof / roundtrip-receiver-lambda — now formally clean after low-arity delegate ABI unification;
//                               pending only mechanical migration to this in-process lane.
// (roundtrip-comparable-meta stays in shell as a full reference-KLIB round-trip.)
@file:OptIn(kotlin.ExperimentalUnsignedTypes::class)
import roundtrip.palette.Color
import kotlinx.roundtrip.palette.StartMode
import roundtrip.cprop.topProp
import roundtrip.cprop.topVar
import roundtrip.cprop.topGetVar
import roundtrip.cprop.Host
import roundtrip.defaultarguments.greet
import roundtrip.defaultarguments.box
import roundtrip.defaultarguments.flags
import roundtrip.defaultarguments.kinds
import roundtrip.defaultarguments.Pt
import roundtrip.nrt.retNonNull
import roundtrip.nrt.takeNonNull
import roundtrip.nrt.retNullable
import roundtrip.nrt.takeNullable
import roundtrip.nrt.retNullableInt
import roundtrip.nrt.UnitAheadHolder
import roundtrip.nrt.NullableCtorHolder
import roundtrip.nrt.NullableValueCtor
import roundtrip.memext.Box
import roundtrip.memext.Lib
import roundtrip.money.Money
import roundtrip.genop.Arr
import roundtrip.nothingret.pick
import roundtrip.nothingret.Boom
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
import roundtrip.nc.Panel as NonConstantDefaultPanel
import roundtrip.nc.column as nonConstantDefaultColumn
import roundtrip.nc.run2
import roundtrip.nc.tagged as nonConstantDefaultTagged
import roundtrip.nc.Rect as NonConstantDefaultRect
import roundtrip.nc.Tri as NonConstantDefaultTri
import roundtrip.nc.Bag as NonConstantDefaultBag
import roundtrip.nc.Pair2 as NonConstantDefaultPair2
import roundtrip.nc.suffixed as nonConstantDefaultSuffixed
import roundtrip.nc.ov as nonConstantDefaultOv
import roundtrip.nc.Panel2 as NonConstantDefaultPanel2
import roundtrip.nc.note as nonConstantDefaultNote
import roundtrip.nc.Seeded as NonConstantDefaultSeeded
import roundtrip.nc.seeds as nonConstantDefaultSeeds
import roundtrip.nc.SeededOrder as NonConstantDefaultSeededOrder
import roundtrip.nc.seedOrder as nonConstantDefaultSeedOrder
import roundtrip.nc.seedMarkP as nonConstantDefaultSeedMarkP
import roundtrip.nc.uf as nonConstantDefaultUf
import roundtrip.nc.Marker as NonConstantDefaultMarker
import roundtrip.nc.scaled as nonConstantDefaultScaled
import roundtrip.nc.tri3 as nonConstantDefaultTri3
import roundtrip.nc.order3 as nonConstantDefaultOrder3
import roundtrip.nc.chain as nonConstantDefaultChain
import roundtrip.nc.genDefaults as nonConstantDefaultGenDefaults
import roundtrip.nc.genPairDefaults as nonConstantDefaultGenPairDefaults
import roundtrip.nc.genMutable as nonConstantDefaultGenMutable
import roundtrip.nc.bumps as nonConstantDefaultBumps
import roundtrip.nc.MemberDefaults as NonConstantDefaultMemberDefaults
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
import roundtrip.extpropref.RefBox
import roundtrip.extpropref.auditLength
import roundtrip.extpropref.auditLast
import roundtrip.extpropref.auditSingleton
import roundtrip.extpropref.auditValue
// #199-①: two same-simple-name GENERIC types (`Cell<T>`) in different producer packages, aliased here.
import roundtrip.genclash.a.Cell as CellA
import roundtrip.genclash.b.Cell as CellB
import roundtrip.genclash.a.cellA
import roundtrip.genclash.b.cellB
// Context parameters (kotlin 2.4 needs no opt-in for them): the producer's `roundtrip.ctxparams` declarations are
// consumed here THROUGH THE DLL, so this is the metadata half of the rule — kotc marks each context slot in the
// emitted parameter list, bir2cir turns the mark into a `[KotlinContextParameter]` marker, and dll2klib restores
// the parameter AS a context parameter. Without the round-trip the same physical method surfaces as a plain leading
// value parameter, and `with(Scale(10)) { scaled(5) }` stops resolving at the module boundary (`scaled(scale, 5)`
// would be required) — a Kotlin SOURCE break, which is exactly what the round-trip metadata exists to prevent.
import roundtrip.ctxparams.Scale
import roundtrip.ctxparams.Holder as CtxHolder
import roundtrip.ctxparams.scaled
import roundtrip.ctxparams.tagged
import roundtrip.ctxparams.labeled
import roundtrip.ctxparams.deco
import roundtrip.ctxparams.gauge
import roundtrip.ctxparams.bumped
import roundtrip.ctxparams.Boxy
import roundtrip.ctxparams.evaluatePlain
import roundtrip.ctxparams.evaluateRecv
import roundtrip.ctxparams.makeCtxFn
import roundtrip.ctxparams.pairFns
import roundtrip.ctxparams.flushA
import roundtrip.ctxparams.GenHolder
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert

class KotlinApiShapeRoundtripTests {
    @TestAttribute
    fun crossModuleContextParameters() {
        with(Scale(10)) {
            ClassicAssert.AreEqual(50, scaled(5))                    // 50    top-level context fun
            ClassicAssert.AreEqual("q:50", "q".deco(5))              // q:50  extension receiver + context
            ClassicAssert.AreEqual(70, CtxHolder(20).combine(5))     // 70    member context fun
            // An omitted NON-CONSTANT default that READS the context parameter: the @KotlinDefault carrier's index
            // counts the context slot, so the splice fills the right position.
            ClassicAssert.AreEqual("5/f10", tagged(5))               // 5/f10 label defaults to "f" + s.factor
            ClassicAssert.AreEqual("5/x", tagged(5, "x"))            // 5/x   nothing omitted
            // A TIER-1 CONSTANT default behind the context slot: filled from the dll2klib metadata, whose
            // per-parameter list is physical — so the context slot shifts `k`'s ordinal.
            ClassicAssert.AreEqual(18, labeled(1))                   // 18    k defaults to 7
            ClassicAssert.AreEqual(13, labeled(1, 2))                // 13    nothing omitted
            // Context PROPERTIES: the accessor is a `get_<name>([__self,] ctx...)` method, restored as a context
            // property rather than an extension property on the CONTEXT type (a different declaration).
            ClassicAssert.AreEqual(30, gauge)                        // 30    top-level context property
            ClassicAssert.AreEqual(12, 2.bumped)                     // 12    extension receiver + context
            ClassicAssert.AreEqual(200, CtxHolder(20).reading)       // 200   member context property
            // A cross-module MEMBER whose omitted default reads the context parameter, with a LATER required arg.
            // The omitted slot must become a positional placeholder bir2cir fills from the callee's @KotlinDefault —
            // dropping it slid `3` into `a` and zero-filled `b`.
            ClassicAssert.AreEqual(1003, CtxHolder(1).pick(b = 3))    // 1003  a defaults to s.factor = 10
            ClassicAssert.AreEqual(203, CtxHolder(1).pick(2, 3))      // 203   nothing omitted
            // The member-EXTENSION form of the same shape (`__self` + context + omitted default + later required arg).
            with(CtxHolder(1)) {
                ClassicAssert.AreEqual(1203, "ab".pickExt(b = 3))     // 1203  a = s.factor + length = 12
                ClassicAssert.AreEqual(203, "ab".pickExt(2, 3))       // 203   nothing omitted
            }
        }
    }

    // A cross-module GENERIC member with an omitted default and a later required argument — the generic call path
    // builds its own argument vector and used to drop the omitted slot, sliding `3` into `a`.
    @TestAttribute
    fun crossModuleGenericMemberDefaultArgs() {
        ClassicAssert.AreEqual("7/3", GenHolder().pick(b = 3))            // 7/3   a defaults to 7
        ClassicAssert.AreEqual("2/3", GenHolder().pick(2, 3))             // 2/3   nothing omitted
        with(GenHolder()) {
            ClassicAssert.AreEqual("x:7/3", "x".pickExt(b = 3))           // x:7/3 __self + omitted default + later arg
            ClassicAssert.AreEqual("x:2/3", "x".pickExt(2, 3))            // x:2/3 nothing omitted
        }
    }

    // A cross-module context FUNCTION TYPE, both forms. The receiver-carrying form is the one that used to bind the
    // CONTEXT argument to the restored extension receiver and silently return the wrong value.
    @TestAttribute
    fun crossModuleContextFunctionTypes() {
        ClassicAssert.AreEqual(6, evaluatePlain { a -> a + 1 })      // 6   context(Boxy) (Int) -> Int, f(5)
        // The RECEIVER-carrying form: `this` must be the RECEIVER Boxy(3), never the context Boxy(10). Restoring the
        // context AS the receiver compiled fine (the ordinary parameter became the unused implicit `it`) and returned
        // 10 — a silently wrong value with no diagnostic anywhere.
        ClassicAssert.AreEqual(3, evaluateRecv { this.v })           // 3
        // The RETURN position of the same type: the restored value takes a receiver AND a context, and `this` must
        // be the receiver. This returned 77 (the context's field, twice) before the lambda lift bound the receiver.
        // Two adjacent context-function-type parameters with DIFFERENT arities (1 and 2): each slot must restore its
        // OWN arity. p sees Scale; q sees Scale AND Boxy.
        ClassicAssert.AreEqual(75, pairFns({ contextOf<Scale>().factor + 5 }, { contextOf<Boxy>().v }))  // 7*10+5
        // The neighbour-meeting shape: `flushA`'s source range ends exactly where the next declaration's begins, and
        // `flushA` also carries a leading comment that moves its FIR start. Its arity must still be 1.
        val fa = flushA()
        with(Scale(4)) { ClassicAssert.AreEqual(4, fa()) }       // 4
        val produced = makeCtxFn()
        with(Scale(7)) { ClassicAssert.AreEqual(37, Boxy(3).produced()) }  // 37  this.v*10 + context.factor
    }

    // #21: top-level generic extension-property accessors are restored from the producer DLL and remain callable
    // through both bound and unbound read/write property references.
    @TestAttribute
    fun crossModuleGenericExtensionPropertyReferences() {
        val unboundPlain = String::auditLength
        ClassicAssert.AreEqual(3, unboundPlain.get("abc"))
        val boundPlain = "hello"::auditLength
        ClassicAssert.AreEqual(5, boundPlain.get())

        val unboundRead = List<String>::auditLast
        ClassicAssert.AreEqual("b", unboundRead.get(listOf("a", "b")))

        val boundRead = listOf(10, 20)::auditLast
        ClassicAssert.AreEqual(20, boundRead.get())

        val collectionValue = String::auditSingleton
        ClassicAssert.AreEqual("[one]", collectionValue.get("one").toString())

        val box = RefBox("before")
        val unboundMutable = RefBox<String>::auditValue
        unboundMutable.set(box, "unbound")
        ClassicAssert.AreEqual("unbound", unboundMutable.get(box))

        val boundMutable = box::auditValue
        boundMutable.set("bound")
        ClassicAssert.AreEqual("bound", boundMutable.get())
    }

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
        ClassicAssert.AreEqual(42, StartMode.DEFAULT.marker()) // class-like enum entry: projected static owner survives re-import
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
    // compile-dependency, + value Nullable<int> structural, + the byte POSITION when a `Unit` node stands ahead of a
    // nullable one in the same slot (writer and reader must agree that `Unit` occupies none).
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
        // A `Unit` node AHEAD of the nullable one in the SAME slot: `Unit` holds no byte in the flattened array, so
        // writing one for it would shift the `String?` onto Unit's and re-import the pair as `Pair<Unit!, String>` —
        // and then the `null` here would not compile.
        val unitAhead: Pair<Unit, String?> = Pair(Unit, null)
        ClassicAssert.AreEqual(-1, UnitAheadHolder().lengthOfSecond(unitAhead))       // -1  the second component really is nullable
        ClassicAssert.AreEqual(3, UnitAheadHolder().lengthOfSecond(Pair(Unit, "abc"))) // 3   and carries a value
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

    // roundtrip-nothing (#135/#197): a CROSS-MODULE re-imported `fun f(): Nothing` — companion-static and
    // top-level — used in a VALUE MERGE. Two facts at once, and both are load-bearing here:
    //   (a) the Nothing return round-trips, so `val r: String = if (c) "kept" else Boom.Companion.boom()` still types as
    //       String rather than widening to Any? — if it widened, this file would not COMPILE;
    //   (b) the merge is well-typed IL. Nothing erases to a CLR `object` return, and letting that reach the merge
    //       put an `object` where a `string` belongs (ilverify StackUnexpected) though the arm always throws, so
    //       the RUN was green. That formal-only gap is exactly what kept this case in the shell lane, which
    //       asserts stdout and never ilverifies; bir2cir now terminates the Nothing arm, so it runs HERE.
    @TestAttribute
    fun crossModuleNothingBranchMerge() {
        ClassicAssert.AreEqual("kept", pickNothing(1))           // kept   the section's golden stdout value
        ClassicAssert.AreEqual("kept2", nothingInThenArm(1))     // kept2  Nothing in the THEN arm
        ClassicAssert.AreEqual("big", nothingInWhenArm(9))       // big    a `when` with a Nothing arm
        ClassicAssert.AreEqual("e", nothingInElvis("e"))         // e      elvis whose right-hand side is Nothing
        ClassicAssert.AreEqual("ok", nothingAsBlockTail(1))      // ok     block arm ENDING in the Nothing call
        ClassicAssert.AreEqual(7, nothingIntoValueSlot(1))       // 7      a VALUE-typed (Int) merge
        // The Nothing arms still throw their own exception, across the module boundary.
        ClassicAssert.AreEqual("boom", try { pickNothing(-1) } catch (e: RuntimeException) { e.message })
        ClassicAssert.AreEqual("x", try { nothingInThenArm(-1) } catch (e: RuntimeException) { e.message })
        ClassicAssert.AreEqual("mid", try { nothingInWhenArm(3) } catch (e: RuntimeException) { e.message })
        ClassicAssert.AreEqual("nul", try { nothingInElvis(null) } catch (e: RuntimeException) { e.message })
        ClassicAssert.AreEqual("tail", try { nothingAsBlockTail(-1) } catch (e: RuntimeException) { e.message })
        ClassicAssert.AreEqual("int", try { nothingIntoValueSlot(-1).toString() } catch (e: RuntimeException) { e.message })
        // BOTH arms Nothing — the conditional itself produces no value. Each arm has a different producer, so both
        // have to run: `n = 1` is the top-level one, `n = -1` the companion-static one.
        ClassicAssert.AreEqual("both", try { nothingInBothArms(1) } catch (e: RuntimeException) { e.message })
        ClassicAssert.AreEqual("boom", try { nothingInBothArms(-1) } catch (e: RuntimeException) { e.message })
        // DEFAULT-PACKAGE producers (Nothingdefault.kt): a root-namespace file class and a root-namespace companion,
        // both resolved with no package qualifier. The `val r: String =` typing is the load-bearing part — it holds
        // only if [KotlinNothing] survived the round trip through default-package attribution.
        val d: String = if (1 >= 0) "kept" else rtDefaultFail("d")
        ClassicAssert.AreEqual("kept", d)
        ClassicAssert.AreEqual("dflt", try { defaultPkgPick(-1) } catch (e: RuntimeException) { e.message })
        ClassicAssert.AreEqual("default-boom", try { defaultPkgBoom(-1) } catch (e: RuntimeException) { e.message })
        ClassicAssert.AreEqual("kept", defaultPkgPick(1))
    }
    private fun defaultPkgPick(n: Int): String = if (n >= 0) "kept" else rtDefaultFail("dflt")
    private fun defaultPkgBoom(n: Int): String = if (n >= 0) "kept" else RtDefaultBoom.Companion.boom()
    // The section's own consumer shape: a companion-static Nothing in the else arm, then a top-level one.
    private fun pickNothing(n: Int): String {
        val r: String = if (n >= 0) "kept" else Boom.Companion.boom()
        return if (n >= 0) r else nothingretFail("x")
    }
    private fun nothingInThenArm(n: Int): String = if (n < 0) nothingretFail("x") else "kept2"
    private fun nothingInWhenArm(n: Int): String =
        when { n > 5 -> "big"; n > 0 -> nothingretFail("mid"); else -> "small" }
    private fun nothingInElvis(s: String?): String = s ?: nothingretFail("nul")
    private val nothingLog = mutableListOf<String>()
    private fun nothingAsBlockTail(n: Int): String =
        if (n >= 0) "ok" else { nothingLog.add("side"); nothingretFail("tail") }
    private fun nothingInBothArms(n: Int): String = if (n >= 0) nothingretFail("both") else Boom.Companion.boom()
    private fun nothingIntoValueSlot(n: Int): Int = if (n >= 0) 7 else nothingretFail("int")
    // NOT covered here: a covariant override returning `Nothing` against a RE-IMPORTED interface. The in-module twin
    // is tests/basic CovariantInterfaceReturnTests.covariantOverrideReturningNothing; cross-module, bir2cir never
    // synthesizes the bridge at all (its synthesizer resolves interface slots from the staged BIR only, so a
    // reference-KLIB interface is invisible to it) and the override claims the slot directly with its erased
    // signature — a TypeLoadException at class load, not this issue's value-merge, and not Nothing-specific.

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
        ClassicAssert.AreEqual(2, nonConstantDefaultColumn(build = { add("hi") }))                     // 2   configure defaults to {} (empty receiver lambda)
        ClassicAssert.AreEqual(3, nonConstantDefaultColumn(configure = { add("ab") }, build = { add("c") })) // 3  both provided (no fill)
        ClassicAssert.AreEqual("ok", run2(body = { }))                                 // ok  pre defaults to {} (empty plain lambda)
        ClassicAssert.AreEqual("z=0", nonConstantDefaultTagged("z"))                                   // z=0 items defaults to emptyList()
        // #235: the CONSTRUCTOR half — a ctor's non-constant default is carried and filled at the omitting `new`.
        ClassicAssert.AreEqual(18, NonConstantDefaultRect(3).area)                                      // 18  h defaults to w * 2 = 6
        ClassicAssert.AreEqual("r", NonConstantDefaultRect(3).tag)                                      // r   a later Tier-1 const still fills
        ClassicAssert.AreEqual(12, NonConstantDefaultRect(3, 4).area)                                   // 12  h provided, no fill
        ClassicAssert.AreEqual("z", NonConstantDefaultRect(3, tag = "z").tag)                           // z   omit the MIDDLE default, name a later arg
        ClassicAssert.AreEqual(6, NonConstantDefaultRect(3, tag = "z").h)                               // 6   the omitted middle still filled from w
        ClassicAssert.AreEqual(203, NonConstantDefaultTri(2).c)                                         // 203 chain: b = a + 1 = 3, c = a * 100 + b
        ClassicAssert.AreEqual(210, NonConstantDefaultTri(2, 10).c)                                     // 210 b provided, c still filled
        ClassicAssert.AreEqual(7, NonConstantDefaultTri(2, 10, 7).c)                                    // 7   nothing omitted
        ClassicAssert.AreEqual(1, NonConstantDefaultBag().size)                                         // 1   items = emptyList(), n = 1
        ClassicAssert.AreEqual(5, NonConstantDefaultBag(n = 5).size)                                    // 5   omit a leading non-const default
        // The argument the default reads is the CONSUMER's own instance read, so the spliced default contains a `this`
        // that belongs to the CALLER. Only a `this` in the CARRIER means "the callee read its receiver".
        ClassicAssert.AreEqual(32, NonConstantDefaultCtorDefaultHost(4).rectArea())                     // 32  w = 4, h = w * 2 = 8
        ClassicAssert.AreEqual(405, NonConstantDefaultCtorDefaultHost(4).triC())                        // 405 b = a + 1 = 5, c = a * 100 + b
        // Same-arity ctor OVERLOADS: the splice key carries the declared parameter vector, so each resolves its own.
        ClassicAssert.AreEqual("2!", NonConstantDefaultPair2("hi").label)                               // 2!  the (String,String) ctor fills upper, delegates this(2)
        ClassicAssert.AreEqual("7!", NonConstantDefaultPair2(7).label)                                  // 7!  the (Int,String) ctor fills label
        // Same-arity FUNCTION overloads carrying different defaults: keyed by the declared parameter vector, so each
        // call site gets ITS own default instead of whichever declaration the metadata scan reached first.
        ClassicAssert.AreEqual("3/6", nonConstantDefaultOv(3))                                          // 3/6  the Int overload: b = a * 2
        ClassicAssert.AreEqual("x/x!", nonConstantDefaultOv("x"))                                       // x/x! the String overload: b = a + "!"
        // Same NAME and same emitted ARITY (an extension's receiver rides as a leading `__self` parameter), differing in
        // a CLASS position — the pair that broke this lane under a name+arity-only carrier key. Its non-extension half
        // is `nonConstantDefaultTagged("z")` in the block above.
        ClassicAssert.AreEqual("q/q", "q".nonConstantDefaultTagged())                                   // q/q  t = this
        // Same arity again, differing in a NULLABLE REFERENCE position (`String?` lowers to a plain `System.String`).
        ClassicAssert.AreEqual("m/-", nonConstantDefaultNote("m"))                                      // m/-  tag defaults to null
        ClassicAssert.AreEqual("5/7", nonConstantDefaultNote(5))                                        // 5/7  the Int overload's own default
        // An UNSIGNED parameter beside a class-typed sibling: two spellings of one type must fold to one key.
        ClassicAssert.AreEqual("u7/1", nonConstantDefaultUf(7u))                                        // u7/1 the UInt overload's own default
        ClassicAssert.AreEqual("mx/2", nonConstantDefaultUf(NonConstantDefaultMarker("x")))                             // mx/2 the class overload's own default
        // A `: super(…)` omitting a cross-module base's non-constant default: a delegation is a call site too.
        ClassicAssert.AreEqual(18, NonConstantDefaultSuperSub(3).area)                                  // 18   h = w * 2 = 6
    }

    // #34/#42: the cross-module carrier distinguishes dispatch, extension and enclosing receivers, and carries
    // closure/SAM/suspend-lambda construction facts instead of poisoning those default shapes.
    @TestAttribute
    fun receiverAwareDefaultCarriers() {
        val m = NonConstantDefaultMemberDefaults(3)
        ClassicAssert.AreEqual(22003, m.scale(2, c = 3))                                // middle omission + later arg
        ClassicAssert.AreEqual(106, m.viaDispatch(1))                                  // dispatch receiver
        ClassicAssert.AreEqual(5, m.viaCapture(2))                                     // capturing lambda: a + k
        ClassicAssert.AreEqual(5, m.viaSam(2))                                         // capturing SAM: a + k
        ClassicAssert.AreEqual(5, m.viaSuspendCarrier(2))                              // capturing suspend lambda
        ClassicAssert.AreEqual(3, m.inlineDispatch { it })                             // inline dispatch receiver
        with(m) {
            ClassicAssert.AreEqual(6, 3.inlineBoth { it })                             // dispatch + extension
        }
        ClassicAssert.AreEqual(5, m.inlineCapture(2, body = { it }))                    // inline capturing default
    }

    // #235: a value the CROSS-MODULE carrier splices is evaluated exactly ONCE, and binding it does not reorder the
    // call's other values. Each `calls`/`log` assertion is the load-bearing one — the values pass either way.
    @TestAttribute
    fun nonConstDefaultArgsEvaluateOnce() {
        val a = NonConstantDefaultEvalCounter()
        ClassicAssert.AreEqual("h/h", a.s().nonConstantDefaultSuffixed())                               // h/h  the EXTENSION RECEIVER a `= this` default reads
        ClassicAssert.AreEqual(1, a.calls)                                              // 1    once, not once per splice

        val b = NonConstantDefaultEvalCounter()
        ClassicAssert.AreEqual(44, nonConstantDefaultScaled(b.n()))                                     // 44   a = 4, b = a * 10
        ClassicAssert.AreEqual(1, b.calls)

        val c = NonConstantDefaultEvalCounter()
        ClassicAssert.AreEqual(405, nonConstantDefaultTri3(c.n()))                                      // 405  a read by BOTH b's and c's defaults
        ClassicAssert.AreEqual(1, c.calls)                                              // 1    (was 4 — once per spliced read)

        val d = NonConstantDefaultEvalCounter()
        ClassicAssert.AreEqual("1/2/20", nonConstantDefaultOrder3(d.a(), d.b()))                        // r = q * 10
        ClassicAssert.AreEqual("ab", d.log)                                             // ab   p before q, and q ONCE (was "abb")

        val e = NonConstantDefaultEvalCounter()
        ClassicAssert.AreEqual(32, NonConstantDefaultRect(e.n()).area)                                  // 32   a ctor argument the default reads
        ClassicAssert.AreEqual(1, e.calls)

        // A side-effecting DEFAULT that a later default reads: filled once, then read from the temp.
        val before = nonConstantDefaultBumps
        ClassicAssert.AreEqual(3030, nonConstantDefaultChain(1))                                        // b = bump() = 3, c = b * 10 = 30
        ClassicAssert.AreEqual(1, nonConstantDefaultBumps - before)                                     // 1    bump() ran once (was 2)
        // The same shape at a `: super(…)`, where the args ride the constructor DECLARATION.
        val seedsBefore = nonConstantDefaultSeeds
        val sub = NonConstantDefaultSeededSub()
        ClassicAssert.AreEqual(3, sub.a)                                                // 3    a = seed()
        ClassicAssert.AreEqual(30, sub.b)                                               // 30   b = a * 10, reading the binding
        ClassicAssert.AreEqual(1, nonConstantDefaultSeeds - seedsBefore)                                // 1    seed() ran once
        // ORDER at that same delegation: the value the `: super(…)` SUPPLIES runs before the base's defaults. The args
        // ride the constructor declaration, so the plan lowers to `preStmts` emitted ahead of the base call.
        val ordered = NonConstantDefaultSeededOrderSub()
        ClassicAssert.AreEqual(2, ordered.p)                                            // 2    the supplied argument
        ClassicAssert.AreEqual(3, ordered.a)                                            // 3    a = seedMarkD()
        ClassicAssert.AreEqual(30, ordered.b)                                           // 30   b = a * 10
        ClassicAssert.AreEqual("pd", nonConstantDefaultSeedOrder)                                       // pd   supplied first, then the default

        // A GENERIC callee's non-constant default, filled in a NON-GENERIC consumer. The carrier is the default as the
        // CALLEE wrote it, so its type parameter rides it as a positional type variable and the splice has to close
        // that frame against THIS call site. Left open it erased to `Any`: the values were right and the program ran,
        // but the object built for the slot had the wrong runtime type — and the IL did not verify.
        ClassicAssert.AreEqual(0, nonConstantDefaultGenDefaults<String>())                              // 0    both defaults filled
        ClassicAssert.AreEqual(1, nonConstantDefaultGenPairDefaults<String>())                          // 1    ...through a nested type arg
        // Observable at RUNTIME, not only in the metadata: the constructed list's own element type.
        val gm = nonConstantDefaultGenMutable<String>()
        gm.add("s")
        ClassicAssert.AreEqual("s", gm[0])
        ClassicAssert.IsTrue(elementTypeName(gm).contains("String"))                     // was System.Object
    }

    // The runtime element type of a constructed list, read off the CLR type itself — the fact a value-only assertion
    // cannot see, because the values are correct either way.
    private fun elementTypeName(xs: MutableList<String>): String = (xs as Any)::class.qualifiedName ?: ""


    // roundtrip-comparable (#179): a `class C : Comparable<C>` — </>/<=/>= + sorted() resolve+run cross-module
    // (dll2klib restores operator compareTo + the kotlin.Comparable supertype; bir2cir binds compareTo->CompareTo).
}

// #235: omits a cross-module ctor's non-constant default while passing an argument that reads THIS instance — the
// filled default therefore embeds the consumer's own `this`, which must not be mistaken for the callee reading a receiver.
class NonConstantDefaultCtorDefaultHost(val n: Int) {
    fun rectArea(): Int = NonConstantDefaultRect(n).area
    fun triC(): Int = NonConstantDefaultTri(this.n).c
}

// #235: counts how often a side-effecting value the cross-module carrier SPLICES actually runs. Kotlin evaluates a
// receiver and each argument once; before the splice bound them to a temp, each spliced read re-ran the expression
// (a value read by two defaults ran four times, and `order3(a(), b())` logged "abb").
// #235: omits the cross-module base's non-constant default at its `: super(…)`, and (NonConstantDefaultSeededSub) omits BOTH of a
// base's defaults where the first is side-effecting and the second reads it.
class NonConstantDefaultSuperSub(w: Int) : NonConstantDefaultPanel2(w)
class NonConstantDefaultSeededSub : NonConstantDefaultSeeded()
// ...and one whose `: super(…)` SUPPLIES a side-effecting value ahead of the base's own filled defaults.
class NonConstantDefaultSeededOrderSub : NonConstantDefaultSeededOrder(nonConstantDefaultSeedMarkP())

class NonConstantDefaultEvalCounter {
    var calls = 0
    fun s(): String { calls++; return "h" }
    fun n(): Int { calls++; return 4 }
    var log = ""
    fun a(): Int { log += "a"; return 1 }
    fun b(): Int { log += "b"; return 2 }
}

class ComparableUnsignedAndPropertyRoundtripTests {
    @TestAttribute
    fun comparableClass() {
        ClassicAssert.IsTrue(Ver(3) < Ver(5))                   // True   `<`  -> restored operator compareTo
        ClassicAssert.IsTrue(Ver(3) < 5)                        // True   lowercase sibling compareTo(Int) stays verbatim
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
    // property DIRECTLY (`import roundtrip.tlval.greeting`), NOT via a re-exposing function — proving dll2klib's
    // The reference KLIB surfaces the field-backed val/var from the BUILT dll. Covers a
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
    // dll2klib dropped the namespace (bare `Cell`, last-wins) the return would resolve to the WRONG package's type
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
