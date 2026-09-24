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
open class LocalFactoryBase {
    val item: Narrow get() = Narrow("local getter")
    val optional: Narrow get() = Narrow("local optional")
    fun make(): Narrow = Narrow("local method")
    fun <T> makeFrom(seed: T, text: String): Narrow = Narrow(text)
}
class LocalFactory : LocalFactoryBase(), Factory

class InheritedCovariantSlotTests {
    @TestAttribute
    fun exportedBridgeOwnersRetainTheirNarrowKotlinSurface() {
        val exported = ExportedFactory()
        val narrowProperty: Narrow = exported.item
        val narrowMethod: Narrow = exported.make()
        val narrowGeneric: Narrow = exported.makeFrom(7, "exported generic")
        check(narrowProperty.text == "base getter")
        check(narrowMethod.text == "base method")
        check(narrowGeneric.text == "exported generic")
        val factory: Factory = exported
        check(factory.item.text == "base getter")
        check(factory.make().text == "base method")
        val derived = ExportedDerived("before")
        val channel: Channel<String> = derived.channel
        val producer: Producer<String> = derived
        producer.channel.send("after")
        check(channel === derived)
        check(channel.last == "after")
    }

    @TestAttribute
    fun importedBaseFillsImportedGenericAndConcreteInterfaceGetters() {
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
        check(middle.optional?.text == "optional")
        check(middle.makeFrom(1, "integer argument").text == "integer argument")
        check(middle.makeFrom("seed", "string argument").text == "string argument")
        val local: Factory = LocalFactory()
        check(local.item.text == "local getter")
        check(local.optional?.text == "local optional")
        check(local.make().text == "local method")
        check(local.makeFrom(2, "local generic").text == "local generic")
        val leaf: Factory = FactoryLeaf()
        check(leaf.item.text == "leaf getter")
        check(leaf.make().text == "leaf method")
    }
}
