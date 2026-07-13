import ChainNs.IMid

class Cell(val v: Int) : IMid<Int> {
    override fun Get(): Int = v                 // inherited through IBase<Int> — value-type slot
    override fun Rank(v: Int): Int = v * 2      // declared on IMid<Int> — value-type slot
}

fun main() {
    val c = Cell(21)
    println(c.Get())
    println(c.Rank(5))
    val m: IMid<Int> = c
    println(m.Get() + m.Rank(1))
}
