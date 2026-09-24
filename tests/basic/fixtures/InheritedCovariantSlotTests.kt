package inheritedcovariantlocal

import NUnit.Framework.TestAttribute

interface Sink<in T> { fun send(value: T) }
interface Channel<T> : Sink<T> { val last: T }
interface Producer<in T> { val channel: Sink<T> }
open class Base<T>(private var value: T) : Channel<T> {
    override fun send(value: T) { this.value = value }
    override val last: T get() = value
    val channel: Channel<T> get() = this
}
class Derived<T>(value: T) : Base<T>(value), Producer<T>
class Concrete : Base<String>("before"), Producer<String>

open class Value(val text: String)
class Narrow(text: String) : Value(text)
interface Factory {
    val item: Value
    val optional: Value?
    fun make(): Value
    fun <T> makeFrom(seed: T, text: String): Value
}
open class FactoryBase {
    open val item: Narrow get() = Narrow("base getter")
    val optional: Narrow get() = Narrow("optional")
    open fun make(): Narrow = Narrow("base method")
    fun <T> makeFrom(seed: T, text: String): Narrow = Narrow(text)
}
open class FactoryMiddle : FactoryBase(), Factory
class FactoryLeaf : FactoryMiddle() {
    override val item: Narrow get() = Narrow("leaf getter")
    override fun make(): Narrow = Narrow("leaf method")
}

class InheritedCovariantSlotTests {
    @TestAttribute
    fun inheritedGenericAndConcreteGettersFillNewInterfaceSlots() {
        val strings = Derived("before")
        val producer: Producer<String> = strings
        check(producer.channel === strings)
        producer.channel.send("after")
        check(strings.last == "after")
        val star: Producer<*> = strings
        check(star.channel === strings)
        val integers = Derived(1)
        val integerProducer: Producer<Int> = integers
        integerProducer.channel.send(2)
        check(integers.last == 2)
        val concrete = Concrete()
        val concreteProducer: Producer<String> = concrete
        concreteProducer.channel.send("concrete")
        check(concrete.last == "concrete")
    }

    @TestAttribute
    fun inheritedCovariantGettersAndMethodsKeepVirtualDispatch() {
        val middle: Factory = FactoryMiddle()
        check(middle.item.text == "base getter")
        check(middle.make().text == "base method")
        check(middle.optional?.text == "optional")
        check(middle.makeFrom(1, "integer argument").text == "integer argument")
        check(middle.makeFrom("seed", "string argument").text == "string argument")
        val leaf: Factory = FactoryLeaf()
        check(leaf.item.text == "leaf getter")
        check(leaf.make().text == "leaf method")
    }
}
