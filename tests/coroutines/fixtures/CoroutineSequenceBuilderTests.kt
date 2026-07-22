// CorB batch — the sequence-builder cold-core + external-generic-base coroutine-context family. The `sequence{}`
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
// ILVERIFY: genbaseext's declared `get_key()` carries the incidental CoroutineContext.Key star-projection
// covariance finding (GitHub #12, formal-only, runtime-safe). In the per-case bash gate it was a verify-compiler-tests.sh
// XFAIL_ILVERIFY [genbaseext] entry; migrated it becomes a finding against DotKt.Tests.Coroutines.dll, baseline-
// listed in tests/run-ilverify.sh (ILVERIFY_XFAIL "CorBGbeBase::get_key()").
//
// Top-level names carry a per-case token (`gs`/`gbe`) under the shared `corB`/`CorB` prefix so they can't clash
// with sibling coroutine fixtures or the stdlib within this single assembly.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.AbstractCoroutineContextKey

// ---- il-genseq -----------------------------------------------------------------------------------------------
fun <T> corBGsWrap(x: T) = sequence { yield(x) }.toList()

// ---- il-genbaseext -------------------------------------------------------------------------------------------
// A NON-GENERIC object over an EXTERNAL (stdlib) generic base with CONCRETE type args — the kotlinx.coroutines
// `CoroutineDispatcher.Key : AbstractCoroutineContextKey<ContinuationInterceptor, CoroutineDispatcher>` shape.
abstract class CorBGbeBase : CoroutineContext.Element {
    override val key: CoroutineContext.Key<*> get() = Key
    companion object Key : CoroutineContext.Key<CorBGbeBase>
}

class CorBGbeDerived : CorBGbeBase()

@OptIn(ExperimentalStdlibApi::class)
object CorBGbeDerivedKey : AbstractCoroutineContextKey<CorBGbeBase, CorBGbeDerived>(CorBGbeBase, { it as? CorBGbeDerived })

class CoroutineSequenceBuilderTests {
    @TestAttribute
    fun genericColdSequence() {
        assertEquals("[5]", corBGsWrap(5).toString())     // [5]
        assertEquals("[hi]", corBGsWrap("hi").toString()) // [hi]
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
        // The DECLARATION of CorBGbeDerivedKey (external generic base SetParent-resolved via MakeGenericType) is the
        // coverage — the former main only printed "ok". Do NOT reference CorBGbeDerivedKey (forcing its .cctor hits a
        // SEPARATE #12 covariance-erasure). Reading get_key on the derived instance resolves through the external
        // generic base to the companion Key (formal-only covariance finding, baseline-listed; runtime-safe).
        val key = CorBGbeDerived().key
        assertEquals(CorBGbeBase.Key, key)   // former golden: "ok"
    }

    @TestAttribute
    fun genericCapturingSequenceGenerator() {
        assertEquals("[1, 2, 4]", generateSequence(1) { it * 2 }.take(3).toList().toString())
        assertEquals("[a, ab, abb]", generateSequence("a") { it + "b" }.take(3).toList().toString())
        assertEquals(18, generateSequence(3) { it + 1 }.take(4).sum())
    }
}
