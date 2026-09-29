package roundtrip.innerdelegation

open class DelegationOuter<T>(val seed: T) {
    var reads = 0
    var trace = ""
    fun readSeed(): T { reads++; trace += "D"; return seed }
    fun supplied(): Int { trace += "A"; return 7 }

    open inner class Base<U>(val own: U, val value: T = readSeed())

    open inner class Middle<B>(val middle: B) {
        open inner class Leaf<C>(val own: C, input: T, val value: T = input, val fromMiddle: B = middle)
    }
}

open class DelegationBridge<X, Y>(seed: Y, val extra: X) : DelegationOuter<Y>(seed)

class LocalDelegation<T>(seed: T) : DelegationOuter<T>(seed) {
    inner class Child : Base<Int>(supplied()) {
        init { trace += "B" }
    }
}

class StringDelegation : DelegationOuter<String>("local") {
    inner class Child : Base<Int> {
        constructor(own: Int) : super(own)
        constructor() : this(17)
    }
}

fun checkSecondaryDelegation() {
    val secondary = StringDelegation()
    val child = secondary.Child()
    check(child.value == "local" && child.own == 17 && secondary.reads == 1)
}
