package roundtrip.selectedbounds

import RoundtripDependentNative.Base
import RoundtripDependentNative.IJoinedValue
import RoundtripDependentNative.ILeft
import RoundtripDependentNative.IRight

interface NominalFactory<K : Base> { fun <A : K> make(): Any }
interface JointFactory<K> where K : Base, K : ILeft, K : IRight { fun <A : K> make(): Any }
open class Concrete : Base(), ILeft, IRight
class Item : Concrete()
class Product<T> : IJoinedValue<T> where T : Base, T : ILeft, T : IRight
class LocalFactory : NominalFactory<Concrete>, JointFactory<Concrete> {
    override fun <A : Concrete> make(): Any = Product<A>()
}
