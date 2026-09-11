package genericunitslots

var effects: Int = 0
interface Source<T> { fun get(): T }
interface DerivedSource<T> : Source<T>
interface FixedUnitSource : Source<Unit>
interface PlainSource { fun get() }
open class UnitSource : Source<Unit>, PlainSource {
    override fun get() { effects++ }
}
open class PlainBody { open fun get() { effects += 10 } }
class InheritedBody : PlainBody(), Source<Unit>
abstract class GenericBase<T> : Source<T> { abstract override fun get(): T }
open class UnitBase : GenericBase<Unit>() {
    override fun get() { effects++ }
}
interface UnitDefault : Source<Unit> {
    override fun get() { effects++ }
}
class DefaultBody : UnitDefault
class GenericOwner<T>(private val value: T) : Source<T> {
    override fun get(): T = value
}
interface PropertySource<T> { val value: T }
class UnitPropertySource : PropertySource<Unit> { override val value: Unit get() = Unit }
class NullableUnitSource : Source<Unit?> { override fun get(): Unit? = null }
open class FinalBody { fun get() { effects++ } }
interface ArgumentSource<P, R> { fun accept(value: P): R }
open class ArgumentBody<T> {
    open fun accept(value: T) { check(value == "argument"); effects++ }
}
open class ComparableBody { open fun compareTo(other: Int): Int = other + 1 }
