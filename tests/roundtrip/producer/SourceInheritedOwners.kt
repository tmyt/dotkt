package roundtrip.sourceowner

class Cell<T>(var value: T)
open class Base<T>(value: T?) { val cell = Cell<T?>(value) }
class Derived<A, B>(val tag: A, value: B?) : Base<B>(value)
class NullableDerived(value: Int?) : Base<Int?>(value)

interface Contract<T> { val box: Cell<T?> }
open class ContractBase<T>(value: T?) : Contract<T> {
    override val box = Cell<T?>(value)
}
class ContractDerived<A, B>(val tag: A, value: B?) : ContractBase<B>(value)
