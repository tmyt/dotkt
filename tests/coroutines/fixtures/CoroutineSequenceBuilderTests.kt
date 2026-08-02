// feature fixture — the sequence-builder cold-core + external-generic-base coroutine-context family. The `sequence{}`
// builder shares the coroutine cold-core lowering (yield -> a suspension point), so it belongs in this lane; no
// blockOn harness is needed (the enumerator drives directly). genbaseext exercises the external generic base
// (`AbstractCoroutineContextKey`) SetParent/MakeGenericType emit path. Each former case's `main` + stdout-golden
// becomes one @TestAttribute method preserving every value 1:1 (`// <expected>`).
//
// Coverage preserved (old case -> method):
//   il-genseq      -> genseq_genericColdSequence         (sequence{yield(x)} in a generic fn; SequenceBuilderIterator)
//   il-seqyieldall -> seqyieldall_yieldAllOverloadPick    (BUG Y: yieldAll cold-entry `sig` overload disambiguation)
//   il-genbaseext  -> genbaseext_externalGenericBaseConcreteArgs (concrete base-arg EMIT via MakeGenericType)
//
// The former per-case gate baselined a formal-only #12 finding for genbaseext's `get_key()`. Existential
// star-projection metadata and bir2cir lowering now preserve this shape without a finding; the whole-assembly
// ILVerify gate therefore carries no exception for it.
//
// Top-level names distinguish the generic sequence and external-generic-base features under descriptive
// `sequenceBuilderGeneric` / `SequenceBuilderExternalGenericBase` stems.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.AbstractCoroutineContextKey

// ---- il-genseq -----------------------------------------------------------------------------------------------
fun <T> sequenceBuilderGenericWrap(x: T) = sequence { yield(x) }.toList()

// ---- il-genbaseext -------------------------------------------------------------------------------------------
// A NON-GENERIC object over an EXTERNAL (stdlib) generic base with CONCRETE type args — the kotlinx.coroutines
// `CoroutineDispatcher.Key : AbstractCoroutineContextKey<ContinuationInterceptor, CoroutineDispatcher>` shape.
abstract class SequenceBuilderExternalGenericBaseElement : CoroutineContext.Element {
    override val key: CoroutineContext.Key<*> get() = Key
    companion object Key : CoroutineContext.Key<SequenceBuilderExternalGenericBaseElement>
}

class SequenceBuilderExternalGenericBaseConcreteElement : SequenceBuilderExternalGenericBaseElement()

@OptIn(ExperimentalStdlibApi::class)
object SequenceBuilderExternalGenericBaseConcreteKey : AbstractCoroutineContextKey<SequenceBuilderExternalGenericBaseElement, SequenceBuilderExternalGenericBaseConcreteElement>(SequenceBuilderExternalGenericBaseElement, { it as? SequenceBuilderExternalGenericBaseConcreteElement })

class CoroutineSequenceBuilderTests {
    @TestAttribute
    fun genericColdSequence() {
        assertEquals("[5]", sequenceBuilderGenericWrap(5).toString())     // [5]
        assertEquals("[hi]", sequenceBuilderGenericWrap("hi").toString()) // [hi]
    }

    @TestAttribute
    fun yieldAllOverloadPick() {
        val s = sequence {
            yield("a")
            yieldAll(listOf("b", "c"))
        }
        assertEquals("a,b,c", s.toList().joinToString(","))   // a,b,c
    }

    @TestAttribute
    fun externalGenericBaseConcreteArgs() {
        // The DECLARATION of SequenceBuilderExternalGenericBaseConcreteKey (external generic base SetParent-resolved via MakeGenericType) is the
        // coverage — the former main only printed "ok". Do NOT reference SequenceBuilderExternalGenericBaseConcreteKey (forcing its .cctor hits a
        // SEPARATE static-initialization path). Reading get_key on the derived instance resolves through the external
        // generic base to the companion Key and is required to remain ILVerify-clean.
        val key = SequenceBuilderExternalGenericBaseConcreteElement().key
        assertEquals(SequenceBuilderExternalGenericBaseElement.Key, key)   // former golden: "ok"
    }

    @TestAttribute
    fun genericCapturingSequenceGenerator() {
        assertEquals("[1, 2, 4]", generateSequence(1) { it * 2 }.take(3).toList().toString())
        assertEquals("[a, ab, abb]", generateSequence("a") { it + "b" }.take(3).toList().toString())
        assertEquals(18, generateSequence(3) { it + 1 }.take(4).sum())
    }
}
