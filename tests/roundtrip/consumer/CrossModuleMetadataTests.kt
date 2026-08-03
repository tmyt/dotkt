// Migrated ktproj-* MSBuild-E2E battery (was the former MSBuild runner `kt <name> …` blocks): each cross-module
// .ktproj graph became a producer (package-separated in ../producer, or ../producer-mpp for the MPP cases) consumed
// here via <ProjectReference> as its BUILT dll (dll2klib re-import, NOT source — the DLL-not-source invariant,
// design §3), asserting the same golden 1:1 as ClassicAssert value asserts (`// <expected>` trailing comments).
//
// Migrated cases (9 -> 9 @TestAttribute methods; the golden was the .ktproj's printed stdout):
//   dotktpkg     <- ktproj-dotktpkg     (#26)  retired dotkt.* reservation + cross-module captured local
//   genmember    <- ktproj-genmember    (#33)  cross-module generic member whose return is an OPEN owner tv
//   genov        <- ktproj-genov        (#25)  cross-module generic top-level fn in a same-name overload set
//   genq         <- ktproj-genq         (#18)  generic Holder<T?> nested-nullable-generic round-trip
//   listparam    <- ktproj-listparam    (#27)  kotlin.collections.* params -> BCL ifaces reverse-mapped back
//   nestedlist   <- ktproj-nestedlist   (#29)  nested-generic-collection Root-V collapse round-trip
//   reprop       <- ktproj-reprop       (#17)  kotlinx.-packaged property get/set accessor lowering
//   injectemit   <- ktproj-injectemit   (#15)  nested-glob source-wins-over-ref (local-vs-ref same-FQN — see below)
//   mpp          <- ktproj-mpp          (#119) expect/actual through the MPP source-set split (producer-mpp)
//   genovCommon  <- ktproj-genov-common (#25 residual) generic factory in an MPP COMMON fragment (producer-mpp)
//
// injectemit is the ONE deliberate exception to DLL-not-source: consumer/injectemit/Demo.kt is a LOCAL copy of the
// `demo` package the producer ALSO exports (see ../producer/Demo.kt + injectemit/Demo.kt headers) — reproducing the
// #15 local-over-ref same-FQN collision the ProjectReference graph exists to guard.
//
// genq (#18) and nestedlist (#29) RUN green but each emits ONE runtime-safe, formal-only IL finding the in-process
// ilverify phase rejects — the same cross-module object-erasure / covariant-collection family the existing baseline
// tracks (#123/#170). They are XFAIL-listed by method in tests/run-ilverify.sh (genq: the erased Vault<object>
// factory return vs the restored Vault<string> slot; nestedlist: the Root-V-collapsed IList<int32> vs an expected
// IReadOnlyCollection<int32>). verify-ktproj never ran ilverify, so these gaps surface for the first time here.
//
// Most producer types carry case-unique simple names (Signal/Vault/Slot/Crate/Store, GenovRef) so an unrelated case's
// semantics aren't perturbed by a name clash. But the #199 same-simple-name collision is now FIXED (dll2klib emits
// namespace-qualified reference tokens), so three collisions are DELIBERATELY RESTORED as its regression guards: the
// two `Arr<T>` (kotlinx.genov.Arr in RoundtripProducer + kotlinx.genovc.Arr in RoundtripProducerMpp — #199-③, a
// generic factory RETURN across dlls) and `Ext.Widget` vs `Inherit.Widget` (#199-② in tests/interop). Each binds to
// the correct type only because dll2klib no longer drops the namespace. The cases test the #-numbered semantics.
import dotkt.foo.bar.state
import dotkt.foo.bar.register
import dotkt.foo.bar.fire
import p2.pair2
import p2.wrap
import kotlinx.genov.atomic
import kotlinx.genov.arrOf
import genq.Slot
import genq.GenericSlots
import genq.FunctionSlots
import genq.SlotDerived
import genq.holderOf
import genq.invokeNullable
import genq.unwrapSlot
import genarr.boxedTriple
import genarr.sumPresent
import genarr.joinPresent
import genarr.firstPresent
import genarr.firstTwo
import genarr.crate
import gencoll.Bin
import gencoll.appendPresent
import gencoll.binValue
import gencoll.boxedInts
import gencoll.describe
import gencoll.lookup
import gencoll.nestedCount
import gencoll.newBin
import gencoll.sumPresent as gcSumPresent
import gencoll.joinPresent as gcJoinPresent
import gencoll.firstPresent as gcFirstPresent
import listparam.takesList
import listparam.takesMutable
import listparam.takesMap
import listparam.makeHolder
import nestedlist.useNested
import nestedlist.boxOfList
import nestedlist.stateOfList
import nestedlist.boxOfMutable
import nestedlist.useNestedMutable
import kotlinx.cell.makeCell
import demo.hello
import demo.Plain
import mpp.app.Greeter
import kotlinx.genovc.arrOfNulls
import starprojection.StarKey
import starprojection.starOwner
import starprojection.isConcreteStarKey
import suspendcompanion.CompanionSuspendApi
import suspendnullable.NullableSuspendHolder
import suspendnullable.invokeNullableSuspend
import suspendnullable.makeNullableSuspend
import suspendnullable.nullTopLevelBlock
import suspendnullable.nullableTopLevelBlock
import suspendnullable.nullableSuspendStep
import suspendref.SuspendRefService
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

class CrossModuleSuspendSink : Continuation<Int> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    var completed: Boolean = false
    var value: Int = 0

    override fun resumeWith(result: Result<Int>) {
        value = result.getOrThrow()
        completed = true
    }
}

fun runCrossModuleSuspend(block: suspend () -> Int): Int {
    val sink = CrossModuleSuspendSink()
    block.startCoroutine(sink)
    ClassicAssert.IsTrue(sink.completed)
    return sink.value
}

private class InvariantTypeProbe<T>(val value: T)

private fun requireNullableSuspendType(
    probe: InvariantTypeProbe<(suspend () -> Int)?>
): (suspend () -> Int)? = probe.value

class CrossModuleCaptureTests {
    // ktproj-dotktpkg (#26 follow-up): a `dotkt.foo.bar` cross-module local captured in a lambda,
    // stored as a delegate, fired later — the captured `Signal<Int>` must survive (not read back NULL/NRE).
    @TestAttribute
    fun capturedSignalInDotktPackage() {
        val c = state(0)                          // cross-module Signal<Int>
        register { c.value = c.value + 1 }        // capture c in a lambda stored as a delegate
        fire()
        fire()
        ClassicAssert.AreEqual(2, c.value)        // 2  — captured cross-module local survives through the stored delegate
    }

    // ktproj-genmember (#33): a DIRECT read of a cross-module generic member whose declared return is the OWNER's
    // type variable (Pair2<A,B>.a/.b) or nests it (Wrap<X>.items = List<X>) — Surface substitutes the tv against the
    // receiver's concrete instantiation so the collection reads Kotlin-format, not the raw BCL List.
}

class GenericMetadataRoundtripTests {
    @TestAttribute
    fun genericMemberTypeVariablesRoundTrip() {
        val p = pair2(7, mutableListOf(1, 2))     // Pair2<Int, MutableList<Int>>
        ClassicAssert.AreEqual(7, p.a)            // 7        tv(type,0) -> Int
        ClassicAssert.AreEqual("[1, 2]", p.b.toString())   // [1, 2]  tv(type,1) -> MutableList<Int>
        val w = wrap(listOf(9, 8, 7))             // Wrap<Int>
        ClassicAssert.AreEqual("[9, 8, 7]", w.items.toString())  // [9, 8, 7]  List<tv(type,0)> -> List<Int>
    }

    // ktproj-genov (#25): a generic top-level fn in a same-name overload set — atomic<String?>(null) must bind the
    // ARITY-1 generic atomic(T) (tag gen1), NOT the arity-2 defaulted sibling; arrOf<String>(3)'s sole overload found.
    @TestAttribute
    fun genericOverloadSetRoundTrips() {
        ClassicAssert.AreEqual(3, arrOf<String>(3).size)      // 3     sole-generic array factory
        ClassicAssert.AreEqual("gen1", atomic<String?>(null).tag)  // gen1  generic arity-1
        ClassicAssert.AreEqual("int", atomic(42).tag)         // int   non-generic Int overload
    }

    // ktproj-genq (#18): a generic factory Holder<T?> + a member Ref<T?> — the [KotlinNullableGeneric] round-trip
    // restores the nested-nullable-generic so the members (size / indexer / cell) resolve (were degraded to Any?).
    @TestAttribute
    fun nullableGenericMembersRoundTrip() {
        val h = holderOf<String>(3)               // Holder<String?>  (factory tv(method,0))
        ClassicAssert.AreEqual(3, h.size)         // 3   member surfaces only when h is NOT Any?
        val e: String? = h[0]                     // null  the get indexer on the generic result
        ClassicAssert.AreEqual("empty", e ?: "empty")   // empty
        val c: Slot<String?> = h.cell()           // Slot<String?>  (member tv(type,0) restored)
        ClassicAssert.AreEqual("cell-null", c.value ?: "cell-null")  // cell-null

        // #147: unlike the return carrier above, parameter / constructor / property declaration slots also need the
        // pre-erasure Slot<T?> shape. `unwrapSlot` must infer T from its parameter without an expected return type.
        // The property and function slots are consumed directly below; their compilation and runtime values require
        // dll2klib to restore the structured nullable-generic carrier from the producer assembly.
        val inferred = unwrapSlot(Slot<String?>("param"))
        ClassicAssert.AreEqual("param", inferred)

        val slots = GenericSlots(Slot<String?>("field"))
        val property: Slot<String?> = slots.propertySlot
        ClassicAssert.AreEqual("field", property.value)

        // A plain function type is a CLR delegate, so its own parameter/return tree must be restored from the same
        // carrier. The explicitly nullable lambda parameter makes T infer as String rather than degrading to Any?.
        val invoked = invokeNullable { value: String? -> value ?: "fn-null" }
        ClassicAssert.AreEqual("fn-null", invoked)
        val functionSlots = FunctionSlots<String> { value -> value ?: "property-null" }
        val functionProperty: (String?) -> String = functionSlots.functionProperty
        ClassicAssert.AreEqual("property-null", functionProperty(null))

        // The public CLR forwarding slot is synthesized after the initial erasure pass. Its parameter carrier must be
        // propagated from SlotConsumer<T> and remain callable through the concrete derived type after re-import.
        val derived = SlotDerived<String>()
        ClassicAssert.AreEqual("bridge", derived.accept(Slot<String?>("bridge")))
    }

    // #86 D2: `Array<X?>` across the module boundary at a VALUE element. The producing assembly emits `object[]` and
    // states the pre-erasure `Array<Int?>` on its carrier; this consumer re-derives BOTH independently, so a
    // disagreement between them is what the case measures — a re-imported surface the consumer's own `Array<Int?>`
    // cannot bind to, or a slot it binds to and then indexes as the wrong array. The `Array<String?>` control keeps
    // its `string[]`: a reference element is not part of D2 and must not move.
    @TestAttribute
    fun nullableValueArraysRoundTrip() {
        val a = boxedTriple(4)                            // Array<Int?> RETURN
        ClassicAssert.AreEqual(3, a.size)                 // 3
        ClassicAssert.AreEqual(4, a[0])                   // 4
        ClassicAssert.IsNull(a[1])                        // null   the absent element survives the boundary
        ClassicAssert.AreEqual(8, a[2])                   // 8
        ClassicAssert.AreEqual(12, sumPresent(a))         // 12  the library's own array back through a PARAM
        val built = arrayOfNulls<Int>(2)                  // an Array<Int?> the CONSUMER builds
        built[0] = 5
        ClassicAssert.AreEqual(5, sumPresent(built))      // 5
        ClassicAssert.AreEqual(0, sumPresent(arrayOfNulls<Int>(2)))          // 0   all-null
        ClassicAssert.AreEqual("a,b", joinPresent(arrayOf("a", null, "b")))  // a,b (reference control)

        // The OPEN `Array<T?>` slot, at the value instantiation and at the reference one.
        ClassicAssert.AreEqual(7, firstPresent(arrayOf<Int?>(null, 7)))      // 7
        ClassicAssert.AreEqual("x", firstPresent(arrayOf<String?>(null, "x")))  // x
        val fresh: Array<Int?> = firstTwo(a)              // an Array<T?> allocated on the far side
        ClassicAssert.AreEqual(2, fresh.size)             // 2
        ClassicAssert.AreEqual(4, fresh[0])               // 4
        ClassicAssert.IsNull(fresh[1])                    // null
        fresh[1] = 9
        ClassicAssert.AreEqual(9, fresh[1])               // 9
        val freshRefs: Array<String?> = firstTwo(arrayOf<String?>("p", null, "q"))
        ClassicAssert.AreEqual("p", freshRefs[0])         // p   (reference control: still a string[])
        ClassicAssert.IsNull(freshRefs[1])                // null

        // The array NESTED in another generic — the carrier whose erasure lands under an array under a type argument.
        val c = crate(a, "tag")
        ClassicAssert.AreEqual(3, c.payload.size)         // 3
        ClassicAssert.AreEqual(4, c.payload[0])           // 4
        ClassicAssert.IsNull(c.payload[1])                // null
        ClassicAssert.AreEqual("tag", c.tag)              // tag
    }

    // #86 — the same boundary for the rest of the reified-argument positions, at a VALUE argument. The producing
    // assembly emits `IReadOnlyList<object>` / `Bin<object>` / `Func<object, string>` and states the pre-erasure
    // `List<Int?>` / `Bin<Int?>` / `(Int?) -> String` on its carrier; this consumer re-derives BOTH independently, so
    // a disagreement between them is what the case measures — a re-imported surface the consumer's own `List<Int?>`
    // cannot bind to, or a slot it binds to and then reads at the wrong element type. `List<String?>` is the
    // control: a reference argument keeps its element type and must not move.
    @TestAttribute
    fun nullableValueCollectionsRoundTrip() {
        val xs = boxedInts(4)                             // List<Int?> RETURN
        ClassicAssert.AreEqual(3, xs.size)                // 3
        ClassicAssert.AreEqual(4, xs[0])                  // 4
        ClassicAssert.IsNull(xs[1])                       // null   the absent element survives the boundary
        ClassicAssert.AreEqual(8, xs[2])                  // 8
        ClassicAssert.AreEqual(12, gcSumPresent(xs))      // 12  the library's own list back through a PARAM
        ClassicAssert.AreEqual(5, gcSumPresent(listOf<Int?>(5, null)))   // 5   a list the CONSUMER builds
        ClassicAssert.AreEqual(0, gcSumPresent(listOf<Int?>(null, null)))// 0   all-null
        ClassicAssert.AreEqual("a,b", gcJoinPresent(listOf("a", null, "b")))  // a,b (reference control)

        // A MUTABLE collection: the callee's writes have to reach the caller's own list, which only holds if both
        // sides name one physical element slot.
        val muts: MutableList<Int?> = mutableListOf(1, null)
        ClassicAssert.AreEqual(2, appendPresent(muts, 6))  // 2   1 and 6 present
        ClassicAssert.AreEqual(4, muts.size)               // 4   the appends are the caller's
        ClassicAssert.AreEqual(6, muts[2])                 // 6
        ClassicAssert.IsNull(muts[3])                      // null

        // A map VALUE, a USER generic in each direction, and a NESTED argument.
        val m: Map<String, Int?> = mapOf("a" to 1, "b" to null)
        ClassicAssert.AreEqual(1, lookup(m, "a"))          // 1
        ClassicAssert.IsNull(lookup(m, "b"))               // null
        ClassicAssert.AreEqual(7, binValue(Bin<Int?>(7)))  // 7   a Bin the CONSUMER builds
        ClassicAssert.IsNull(binValue(Bin<Int?>(null)))    // null
        val b: Bin<Int?> = newBin(3)                       // …and one the library builds
        ClassicAssert.AreEqual(3, b.item)                  // 3
        ClassicAssert.AreEqual(3, nestedCount(listOf(listOf(1, null), listOf<Int?>(null))))   // 3

        // A DELEGATE component: the lifted lambda must declare the slot the re-imported `Func<object, string>` names.
        ClassicAssert.AreEqual("5", describe(5) { v -> v?.toString() ?: "none" })      // 5
        ClassicAssert.AreEqual("none", describe(null) { v -> v?.toString() ?: "none" })// none

        // The OPEN `List<T?>` slot, at the value instantiation and at the reference one.
        ClassicAssert.AreEqual(7, gcFirstPresent(listOf<Int?>(null, 7)))       // 7
        ClassicAssert.AreEqual("x", gcFirstPresent(listOf<String?>(null, "x")))// x
    }

    // ktproj-listparam (#27): kotlin.collections.* params surface as BCL ifaces in the dll; dll2klib reverse-maps
    // them back so listOf/mutableListOf/mapOf values unify with the params (+ generic inference through makeHolder).
    @TestAttribute
    fun collectionParametersRoundTrip() {
        ClassicAssert.AreEqual(2, takesList(listOf("a", "b")))           // 2   List<String> param + listOf
        ClassicAssert.AreEqual(3, takesMutable(mutableListOf(1, 2)))     // 3   MutableList<Int> param (lib adds 99)
        ClassicAssert.AreEqual(2, takesMap(mapOf("x" to 10, "y" to 20))) // 2   Map<String,Int> param + mapOf
        val h = makeHolder(listOf("x", "y"))      // generic inference: T = String
        ClassicAssert.AreEqual(2, h.items.size)   // 2   List<T> property member resolution
    }

    // ktproj-nestedlist (#29): kotlin.collections.* nested INSIDE a user generic (Box<List<T>>/State<List<T>>) must
    // round-trip as List (not collapse to MutableList); a nested MutableList slot still surfaces as MutableList.
    @TestAttribute
    fun nestedGenericCollectionsRoundTrip() {
        val b = boxOfList(listOf("a", "b"))       // Box<List<String>> — value unifies with the List slot
        ClassicAssert.AreEqual(2, useNested(b))   // 2
        val st = stateOfList(listOf(1, 2, 3))     // State<List<Int>>
        ClassicAssert.AreEqual(3, st.value.size)  // 3
        val mb = boxOfMutable(mutableListOf(10, 20))   // Crate<MutableList<Int>> — read/write split preserved
        ClassicAssert.AreEqual(3, useNestedMutable(mb))  // 3  add duplicates v[0] -> [10, 20, 10]
        // (the direct `mb.v` generic-member read — the #33 shape — is covered by genmember's p.b/w.items; omitting
        //  it here keeps this method's emitted IL ilverify-clean, since the read-only vs invariant collection
        //  interface of the Root-V collapse would surface a formal-only IList/IReadOnlyCollection variance finding.)
    }

    // A bounded generic G<*> is represented in CLR by a compiler-generated existential interface. The referenced
    // property's and function parameter's Kotlin signatures must be restored from metadata, never weakened to Any?.
    @TestAttribute
    fun boundedStarProjectionRoundTrips() {
        val key: StarKey<*> = starOwner().key
        ClassicAssert.IsTrue(isConcreteStarKey(key))
    }

    // ktproj-reprop (#17): a direct property get/set on a `kotlinx.`-packaged re-imported type lowers to the
    // get_value/set_value accessor call (the kotlinx. prefix makes NetInteropBinding skip the owner).
}

class PropertyAndSourcePrecedenceTests {
    @TestAttribute
    fun reimportedPropertyAccessors() {
        val c = makeCell(10)
        ClassicAssert.AreEqual(10, c.value)       // 10   cross-module property GET
        c.value = 42                              //      cross-module property SET
        ClassicAssert.AreEqual(42, c.value)       // 42
        ClassicAssert.AreEqual(84, c.doubled())   // 84   member fn reading its own property
    }

    // ktproj-injectemit (#15 EMIT-HALF): `demo.hello`/`demo.Plain` are compiled LOCALLY (consumer/injectemit/Demo.kt,
    // recursive glob) AND exported by the referenced RoundtripProducer.dll — source wins, and bir2cir prefers the
    // LOCAL BIR type over the ref of the same FQN (a local `new demo.Plain`, not a `newClr` -> ilemit conflict).
    @TestAttribute
    fun localSourceWinsOverReferencedMetadata() {
        ClassicAssert.AreEqual(42, hello())       // 42     the local top-level fun, not the referenced dll's copy
        val p = Plain()                           // local `new demo.Plain`, not a `newClr` against the ref
        ClassicAssert.AreEqual("plain", p.tag)    // plain
    }

    // ktproj-mpp (#119): the MPP producer's common `expect class Greeter` + clr `actual` collapse to one Greeter in
    // the emitted dll (the common->platform module split ran through MSBuild); consuming it cross-module resolves say().
}

class MultiplatformMetadataTests {
    @TestAttribute
    fun expectActualRoundTrip() {
        ClassicAssert.AreEqual("Hello from the CLR actual", Greeter().say())  // Hello from the CLR actual
    }

    // ktproj-genov-common (#25 residual): a generic factory in the MPP producer's COMMON fragment (file class
    // GenovCommonKt); the bare name arrOfNulls also lives under GenovAltKt, so bir2cir must promote shapeTypes->sig
    // via kotc's external ownerType even when the by-name ref index can't disambiguate the owner.
    @TestAttribute
    fun commonFragmentGenericFactoryRoundTrip() {
        ClassicAssert.AreEqual(3, arrOfNulls<String>(3).size)  // 3  common-fragment generic factory
    }
}

class SuspendMetadataRoundtripTests {
    // #148: a suspend member on a companion crosses the DLL boundary through dll2klib metadata, is invoked from a
    // consumer-side suspend lambda, and completes through the Kotlin Continuation ABI (not merely declaration emit).
    @TestAttribute
    fun companionSuspendFunctionRoundTripsAndRuns() {
        ClassicAssert.AreEqual(42, runCrossModuleSuspend { CompanionSuspendApi.compute(41) })
    }

    // #67 residual: a DotKt member re-imported from a ProjectReference remains a suspend declaration. Its callable
    // reference must target that member's cold entry through the same newSuspendLambda adapter used in-module.
    @TestAttribute
    fun referencedSuspendMemberCallableReferencesRun() {
        val service = SuspendRefService(40)
        val bound: suspend (Int) -> Int = service::fetch
        ClassicAssert.AreEqual(42, runCrossModuleSuspend { bound(2) })

        val unbound: suspend (SuspendRefService, Int) -> Int = SuspendRefService::fetch
        ClassicAssert.AreEqual(42, runCrossModuleSuspend { unbound(service, 2) })
    }

    // #172: dll2klib must compose the slot's CLR NRT nullable marker over its carried suspend-function shape. Wrapping
    // each imported expression in an invariant probe first captures its independently inferred type: passing that probe
    // to requireNullableSuspendType then compiles only when the DLL slot restored exactly `(suspend () -> Int)?`.
    @TestAttribute
    fun nullableSuspendFunctionTypesRoundTripAndRun() {
        ClassicAssert.AreEqual(42, invokeNullableSuspend { nullableSuspendStep(41) }) // parameter
        ClassicAssert.AreEqual(-1, invokeNullableSuspend(null))

        val returnedProbe = InvariantTypeProbe(makeNullableSuspend(40))               // return
        val returned = requireNullableSuspendType(returnedProbe)
        ClassicAssert.AreEqual(41, runCrossModuleSuspend(returned!!))
        val nullReturnedProbe = InvariantTypeProbe(makeNullableSuspend(-1))
        ClassicAssert.IsNull(requireNullableSuspendType(nullReturnedProbe))

        val holder = NullableSuspendHolder(39)
        val propertyProbe = InvariantTypeProbe(holder.block)                          // property
        val property = requireNullableSuspendType(propertyProbe)
        ClassicAssert.AreEqual(40, runCrossModuleSuspend(property!!))

        val nullPropertyProbe = InvariantTypeProbe(NullableSuspendHolder(null).block)
        ClassicAssert.IsNull(requireNullableSuspendType(nullPropertyProbe))

        val fieldProbe = InvariantTypeProbe(nullableTopLevelBlock)                     // file-class field
        val field = requireNullableSuspendType(fieldProbe)
        ClassicAssert.AreEqual(41, runCrossModuleSuspend(field!!))
        val nullFieldProbe = InvariantTypeProbe(nullTopLevelBlock)
        ClassicAssert.IsNull(requireNullableSuspendType(nullFieldProbe))
    }
}
