package roundtrip.sourceowner

class Cell<T>(var value: T)
open class Base<T>(value: T?) { val cell = Cell<T?>(value) }
class Derived<A, B>(val tag: A, value: B?) : Base<B>(value)
class NullableDerived(value: Int?) : Base<Int?>(value)
class NullableLayer<T>(value: T?) : Base<T?>(value)

interface Contract<T> { val box: Cell<T?> }
open class ContractBase<T>(value: T?) : Contract<T> {
    override val box = Cell<T?>(value)
}
class ContractDerived<A, B>(val tag: A, value: B?) : ContractBase<B>(value)

interface Root<T> { val item: Cell<T?> }
interface Mid<T> : Root<T>
interface Leaf : Mid<Int?>
class LeafImpl(value: Int?) : Leaf { override val item = Cell<Int?>(value) }

interface DefaultRoot<T> { val answer: Int get() = 47 }
interface DefaultMid<T> : DefaultRoot<T>
interface DefaultLeaf : DefaultMid<Int?>
class DefaultLeafImpl : DefaultLeaf
