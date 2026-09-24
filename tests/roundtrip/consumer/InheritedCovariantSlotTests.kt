package inheritedcovariantroundtrip

import NUnit.Framework.TestAttribute
import inheritedcovariantreference.*

class Derived<T>(value: T) : Base<T>(value), Producer<T>
class Concrete : Base<String>("before"), Producer<String>
interface LocalProducer<in T> { val channel: Sink<T> }
class LocalDerived<T>(value: T) : Base<T>(value), LocalProducer<T>
open class FactoryMiddle : FactoryBase(), Factory
class FactoryLeaf : FactoryMiddle() {
    override val item: Narrow get() = Narrow("leaf getter")
    override fun make(): Narrow = Narrow("leaf method")
}

class InheritedCovariantSlotTests {
    @TestAttribute
    fun importedBaseFillsImportedGenericAndConcreteInterfaceGetters() {
        val strings = Derived("before")
        val producer: Producer<String> = strings
        check(producer.channel === strings)
        producer.channel.send("after")
        check(strings.last == "after")
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
    fun importedBaseFillsLocalInterfaceGetter() {
        val value = LocalDerived("before")
        val producer: LocalProducer<String> = value
        check(producer.channel === value)
        producer.channel.send("local")
        check(value.last == "local")
    }

    @TestAttribute
    fun importedCovariantGettersAndMethodsKeepVirtualDispatch() {
        val middle: Factory = FactoryMiddle()
        check(middle.item.text == "base getter")
        check(middle.make().text == "base method")
        val leaf: Factory = FactoryLeaf()
        check(leaf.item.text == "leaf getter")
        check(leaf.make().text == "leaf method")
    }
}
