// C-1: a Kotlin class IMPLEMENTS a real .NET interface (System.IComparable), façade-free.
import clrgen.IComparable
import clrgen.Console

class Money(val cents: Int) : IComparable {
	override fun CompareTo(other: Any?): Int = cents
}

fun main() {
	val c: IComparable = Money(42)   // usable as the .NET interface
	Console.WriteLine(c.CompareTo(null))
}
