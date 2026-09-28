import NUnit.Framework.TestAttribute

open class InnerConstructorBase<T>(val baseValue: T)

class InnerConstructorOuter(val outerValue: Int) {
    var reads = 0
    fun readOuter(): Int { reads++; return outerValue }
    open inner class OuterBase(val received: Int)

    inner class Middle(val middleValue: Int) {
        inner class Primary : InnerConstructorBase<Int>(readOuter() + middleValue) {
            val initialized = outerValue + middleValue
            fun afterConstruction(): Int = outerValue + middleValue
        }
        inner class Secondary : InnerConstructorBase<Int> {
            val initialized: Int
            constructor() : this(readOuter())
            constructor(value: Int) : super(value + middleValue) {
                initialized = outerValue + middleValue
            }
        }
        inner class Derived : OuterBase(readOuter() + middleValue)
        inner class Inlined : InnerConstructorBase<Int>(run { readOuter() + middleValue })
        inner class Defaults : InnerConstructorBase<Int> {
            constructor(value: Int = readOuter()) : super(value + middleValue)
            constructor(marker: String) : this()
        }
        inner class Deeper(val deeperValue: Int) {
            inner class Leaf : InnerConstructorBase<Int>(readOuter() + middleValue + deeperValue)
        }
        inner class Anonymous : InnerConstructorBase<Int>(object {
            fun value(): Int = readOuter() + middleValue
        }.value())
    }
}

class InnerConstructorGenericOuter<O>(val outerValue: O) {
    inner class Middle<M>(val middleValue: M) {
        inner class Leaf : InnerConstructorBase<O>(outerValue) {
            val initialized = middleValue
            fun afterConstruction(): O = outerValue
        }
    }
}

class InnerConstructorDelegationTests {
    @TestAttribute
    fun enclosingReceiversAreAvailableBeforeDelegation() {
        val outer = InnerConstructorOuter(19)
        val middle = outer.Middle(4)
        val primary = middle.Primary()
        check(primary.baseValue == 23)
        check(primary.initialized == 23)
        check(primary.afterConstruction() == 23)
        check(outer.reads == 1)
        val secondary = middle.Secondary()
        check(secondary.baseValue == 23)
        check(secondary.initialized == 23)
        check(outer.reads == 2)
        check(middle.Derived().received == 23)
        check(outer.reads == 3)
        check(middle.Inlined().baseValue == 23)
        check(outer.reads == 4)
        check(middle.Defaults("delegated").baseValue == 23)
        check(outer.reads == 5)
        check(middle.Defaults().baseValue == 23)
        check(outer.reads == 6)
        check(middle.Deeper(2).Leaf().baseValue == 25)
        check(outer.reads == 7)
        check(middle.Anonymous().baseValue == 23)
        check(outer.reads == 8)
    }

    @TestAttribute
    fun genericEnclosingReceiversRetainTheirTypesAcrossDelegation() {
        val reference = InnerConstructorGenericOuter("outer").Middle(31).Leaf()
        check(reference.baseValue == "outer")
        check(reference.initialized == 31)
        check(reference.afterConstruction() == "outer")
        val value = InnerConstructorGenericOuter(17).Middle("middle").Leaf()
        check(value.baseValue == 17)
        check(value.initialized == "middle")
        check(value.afterConstruction() == 17)
        val nullable = InnerConstructorGenericOuter<Int?>(null).Middle("nullable").Leaf()
        check(nullable.baseValue == null)
        check(nullable.initialized == "nullable")
    }
}
