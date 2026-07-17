import kotlin.coroutines.*

// A NON-GENERIC object over an EXTERNAL (stdlib) generic base with CONCRETE type args:
// `object MyDerivedKey : AbstractCoroutineContextKey<MyBase, MyDerived>`. kotc used to emit the base as
// an OPEN bare name — dropping the concrete args — so ilemit failed "cannot resolve .NET type
// kotlin.coroutines.AbstractCoroutineContextKey" (no `2 arity, no MakeGenericType). kotc now emits the
// base's ACTUAL args (`AbstractCoroutineContextKey[MyBase, MyDerived]`) and ilemit SetParent-constructs
// the external `AbstractCoroutineContextKey`2` via MakeGenericType. This is the exact kotlinx.coroutines
// `CoroutineDispatcher.Key : AbstractCoroutineContextKey<ContinuationInterceptor, CoroutineDispatcher>`
// shape (the rc6 port blocker). The generic-subclass-over-in-assembly-base tv-flow is covered by il-genbase.
abstract class MyBase : CoroutineContext.Element {
    override val key: CoroutineContext.Key<*> get() = Key
    companion object Key : CoroutineContext.Key<MyBase>
}

class MyDerived : MyBase()

@OptIn(ExperimentalStdlibApi::class)
object MyDerivedKey : AbstractCoroutineContextKey<MyBase, MyDerived>(MyBase, { it as? MyDerived })

fun main() {
    // MyDerivedKey's TYPE is emitted (and its external generic base SetParent-resolved via MakeGenericType)
    // regardless of use — its mere declaration exercises the concrete base-arg EMIT path that used to fail
    // "cannot resolve .NET type kotlin.coroutines.AbstractCoroutineContextKey". main does NOT reference the
    // object: forcing its .cctor at type-load hits a SEPARATE, pre-existing #12 covariance-erasure
    // (CoroutineContext.Key<Object> violates the invariant Key<E> constraint) — runtime base-ctor over a
    // generic base with the subclass's OWN tv is covered green by il-genbase (`D<T> : Base<T>`).
    println("ok")
}
