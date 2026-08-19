// #325 producer half: the consumer sees this hierarchy only through the emitted DLL and its projected KLIB. Keeping
// the member and mutable property on the inherited interface exercises declaring-owner recovery as well as dispatch.
package roundtrip.constrainedreceiver

interface ReferencedReceiverRoot<X> {
    fun produce(): X
    var slot: X
}

interface ReferencedReceiverLeaf<X> : ReferencedReceiverRoot<String> {
    fun leaf(): Int
}

class ReferencedReceiverIntLeaf(initial: Int) : ReferencedReceiverLeaf<Int> {
    private val seed: Int = initial
    private var current: String = "slot:$initial"
    override fun produce(): String = "produce:$seed"
    override var slot: String
        get() = current
        set(value) { current = value }
    override fun leaf(): Int = 5
}
