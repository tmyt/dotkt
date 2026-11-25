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
class Product<T> : IJoinedValue<T> where T : Base, T : ILeft, T : IRight
class LocalFactory : NominalFactory<Concrete>, JointFactory<Concrete> {
    override fun <A : Concrete> make(): Any = Product<A>()
    override fun <A : Concrete> select(value: A): A = value
}
