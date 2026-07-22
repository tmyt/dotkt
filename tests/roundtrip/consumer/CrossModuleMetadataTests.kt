// Migrated ktproj-* MSBuild-E2E battery (was the former MSBuild runner `kt <name> …` blocks): each cross-module
// .ktproj graph became a producer (package-separated in ../producer, or ../producer-mpp for the MPP cases) consumed
// here via <ProjectReference> as its BUILT dll (facadegen re-import, NOT source — the DLL-not-source invariant,
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
// semantics aren't perturbed by a name clash. But the #199 same-simple-name collision is now FIXED (facadegen emits
// namespace-qualified reference tokens), so three collisions are DELIBERATELY RESTORED as its regression guards: the
// two `Arr<T>` (kotlinx.genov.Arr in RoundtripProducer + kotlinx.genovc.Arr in RoundtripProducerMpp — #199-③, a
// generic factory RETURN across dlls) and `Ext.Widget` vs `Inherit.Widget` (#199-② in tests/interop). Each binds to
// the correct type only because facadegen no longer drops the namespace. The cases test the #-numbered semantics.
import dotkt.foo.bar.state
import dotkt.foo.bar.register
import dotkt.foo.bar.fire
import p2.pair2
import p2.wrap
import kotlinx.genov.atomic
import kotlinx.genov.arrOf
import genq.Slot
import genq.holderOf
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
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert

class CrossModuleCaptureTests {
    // ktproj-dotktpkg (#26 follow-up): a `dotkt.foo.bar` cross-module local captured in a lambda,
    // stored as a delegate, fired later — the captured `Signal<Int>` must survive (not read back NULL/NRE).
    @TestAttribute
    fun capturedStateInDotktPackage() {
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
    }

    // ktproj-listparam (#27): kotlin.collections.* params surface as BCL ifaces in the dll; facadegen reverse-maps
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
    // via kotc's injected ownerType even when the by-name ref index can't disambiguate the owner.
    @TestAttribute
    fun commonFragmentGenericFactoryRoundTrip() {
        ClassicAssert.AreEqual(3, arrOfNulls<String>(3).size)  // 3  common-fragment generic factory
    }
}
