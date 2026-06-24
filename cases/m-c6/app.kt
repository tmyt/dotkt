// C-1: import and use a real .NET enum (System.DayOfWeek), façade-free.
import System.DayOfWeek
import System.Console
fun main() {
	val d: DayOfWeek = DayOfWeek.Friday
	Console.WriteLine(d.toString())
	Console.WriteLine(DayOfWeek.Monday.toString())
}
