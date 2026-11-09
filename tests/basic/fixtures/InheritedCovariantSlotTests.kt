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

abstract class ClassFactoryBase {
    abstract val item: Value
    abstract fun make(): Value?
    abstract fun <T> makeFrom(seed: T, text: String): Value
}
abstract class ClassFactoryNarrow : ClassFactoryBase() {
    abstract override val item: Narrow
    abstract override fun make(): Narrow?
    abstract override fun <T> makeFrom(seed: T, text: String): Narrow
}
open class ClassFactory : ClassFactoryNarrow() {
    var empty = false
    override val item: Narrow get() = Narrow("class getter")
    override fun make(): Narrow? = if (empty) null else Narrow("class method")
    override fun <T> makeFrom(seed: T, text: String): Narrow = Narrow(text)
}
class ClassFactoryLeaf : ClassFactory() {
    override val item: Narrow get() = Narrow("class leaf getter")
    override fun make(): Narrow? = Narrow("class leaf method")
}

private abstract class BoundReturnBase { abstract fun value(): Value }
private class BoundReturn<T : Value>(private val item: T) : BoundReturnBase() { override fun value(): T = item }
private abstract class BoxedReturnBase { abstract fun value(): Comparable<Int> }
private class BoxedReturn : BoxedReturnBase() { override fun value(): Int = 7 }
private abstract class MethodBoundBase { abstract fun <T : Value> choose(value: T): Value }
private class MethodBound : MethodBoundBase() { override fun <T : Value> choose(value: T): T = value }
private abstract class EchoBase { abstract fun <T> echo(value: T): T }
private abstract class EchoMiddle : EchoBase()
private class EchoLeaf : EchoMiddle() { override fun <T> echo(value: T): T = value }
private interface OverloadLeft
private interface OverloadRight
private class LeftValue : OverloadLeft
private class RightValue : OverloadRight
private abstract class OverloadBase {
    abstract fun <T : OverloadLeft> choose(value: T): Int
    abstract fun <T : OverloadRight> choose(value: T): Int
}
private abstract class OverloadMiddle : OverloadBase()
private class OverloadLeaf : OverloadMiddle() {
    override fun <T : OverloadLeft> choose(value: T): Int = 1
    override fun <T : OverloadRight> choose(value: T): Int = 2
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
        val value = Narrow("bounded")
        val bounded: BoundReturnBase = BoundReturn(value)
        check(bounded.value() === value)
        val boxed: BoxedReturnBase = BoxedReturn()
        check(boxed.value().compareTo(7) == 0)
        val methodBound: MethodBoundBase = MethodBound()
        check(methodBound.choose(value) === value)
        val echo: EchoMiddle = EchoLeaf()
        check(echo.echo(7) == 7 && echo.echo("echo") == "echo" && echo.echo<String?>(null) == null)
        val overloads: OverloadMiddle = OverloadLeaf()
        check(overloads.choose(LeftValue()) == 1 && overloads.choose(RightValue()) == 2)
        val middle: Factory = FactoryMiddle()
        check(middle.item.text == "base getter")
        check(middle.make().text == "base method")
        check(middle.optional?.text == "optional")
        check(middle.makeFrom(1, "integer argument").text == "integer argument")
        check(middle.makeFrom("seed", "string argument").text == "string argument")
        val leaf: Factory = FactoryLeaf()
        check(leaf.item.text == "leaf getter")
        check(leaf.make().text == "leaf method")
        val concrete = ClassFactory()
        val classBase: ClassFactoryBase = concrete
        check(classBase.item.text == "class getter" && classBase.make()?.text == "class method")
        check(classBase.makeFrom(7, "class generic").text == "class generic")
        concrete.empty = true
        check(classBase.make() == null)
        val classLeaf: ClassFactoryBase = ClassFactoryLeaf()
        check(classLeaf.item.text == "class leaf getter" && classLeaf.make()?.text == "class leaf method")
    }
}
