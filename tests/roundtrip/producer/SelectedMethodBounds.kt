package roundtrip.selectedbounds

import RoundtripDependentNative.Base
import RoundtripDependentNative.IJoinedValue
import RoundtripDependentNative.ILeft
import RoundtripDependentNative.IRight

interface NominalFactory<K : Base> {
    fun <A : K> make(): Any
    fun <A : K> select(value: A): A = error("expected the overriding implementation")
}
interface JointFactory<K> where K : Base, K : ILeft, K : IRight {
    fun <A : K> make(): Any
    fun <A : K> select(value: A): A
}
open class Concrete : Base(), ILeft, IRight
class Item : Concrete()
class Leaf<T> : Concrete()
class Product<T> : IJoinedValue<T> where T : Base, T : ILeft, T : IRight
interface LocalDefault : NominalFactory<Concrete>, JointFactory<Concrete> {
    override fun <A : Concrete> make(): Any = Product<A>()
    override fun <A : Concrete> select(value: A): A = value
}
class LocalFactory : LocalDefault

open class GenericParent<C> : JointFactory<Concrete> {
    override fun <A : Concrete> make(): Any = "generic"
    override fun <A : Concrete> select(value: A): A = value
}

interface LeftFactory<K> where K : Base, K : ILeft { fun <A : K> make(): Any }
interface RightFactory<K> where K : Base, K : IRight { fun <A : K> make(): Any }
interface DefaultFactory<K : ILeft> { fun <A : K> make(): Any = "default" }
