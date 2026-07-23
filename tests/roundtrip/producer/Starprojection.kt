// A bounded generic star projection has no direct invariant CLR representation. bir2cir emits a non-generic
// existential interface for StarKey<*>, while [KotlinType] must restore the original projection when this DLL is
// consumed as Kotlin. Without the carrier, StarOwner.key and isConcreteStarKey's parameter re-import as Any?.
package starprojection

interface StarElement
interface StarKey<E : StarElement>

class ConcreteStarElement : StarElement
object ConcreteStarKey : StarKey<ConcreteStarElement>

interface StarOwner {
    val key: StarKey<*>
}

private class StarOwnerImpl : StarOwner {
    override val key: StarKey<*> get() = ConcreteStarKey
}

fun starOwner(): StarOwner = StarOwnerImpl()
fun isConcreteStarKey(key: StarKey<*>): Boolean = key === ConcreteStarKey
