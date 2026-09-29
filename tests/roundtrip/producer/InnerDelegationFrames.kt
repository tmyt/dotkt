package roundtrip.innerdelegation

open class DelegationOuter<T>(val seed: T) {
    var reads = 0
    var trace = ""
    fun readSeed(): T { reads++; trace += "D"; return seed }
    fun supplied(): Int { trace += "A"; return 7 }

    open inner class Base<U>(val own: U, val value: T = readSeed())

    open inner class Middle<B>(val middle: B) {
        open inner class Leaf<C>(val own: C, input: T, val value: T = input, val fromMiddle: B = middle)
        open inner class CapturedLeaf<C>(val own: C, val value: T = readSeed(), val fromMiddle: B = middle)
    }
}

open class DelegationBridge<X, Y>(seed: Y, val extra: X) : DelegationOuter<Y>(seed)

class LocalDelegation<T>(seed: T) : DelegationOuter<T>(seed) {
    inner class Child : Base<Int>(supplied()) {
        init { trace += "B" }
    }
    inner class MiddleChild : Middle<Int>(61) {
        inner class CapturedChild : CapturedLeaf<String>("own")
    }
    fun readGrandparentDefault(): T {
        val child = MiddleChild().CapturedChild()
        check(child.fromMiddle == 61 && child.own == "own")
        return child.value
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

class ThisDefaultOuter<T>(val seed: T) {
    inner class Child(val value: T = seed) {
        constructor(marker: Int, token: Boolean) : this()
    }
}

fun checkOmittedThisDefaults() {
    check(ThisDefaultOuter("value").Child(marker = 0, token = true).value == "value")
    check(ThisDefaultOuter(41).Child(marker = 0, token = true).value == 41)
    check(ThisDefaultOuter<Int?>(null).Child(marker = 0, token = true).value == null)
}

class LiftedDelegation<A>(seed: A) : DelegationOuter<A>(seed) {
    fun <M> checkObject(own: M): Boolean {
        val child = object : Base<M>(own) {}
        return child.value == seed && child.own == own
    }
    fun checkLocal(): A {
        class Local : Base<Int>(43)
        return Local().value
    }
}

fun checkLiftedDelegationFrames() {
    val text = LiftedDelegation("lifted")
    check(text.checkObject(47) && text.checkLocal() == "lifted" && text.reads == 2)
    val number = LiftedDelegation(53)
    check(number.checkObject("own") && number.checkLocal() == 53 && number.reads == 2)
    val nullable = LiftedDelegation<Int?>(null)
    check(nullable.checkObject<String?>(null) && nullable.checkLocal() == null && nullable.reads == 2)
}
