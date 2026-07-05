// Grouping.eachCount() — its body reads a value-type-nullable smart-cast (`Int?`) in arithmetic
// (`if (count == null) 1 else count + 1`), the C1 value-slot-unwrap class.
fun main() {
    println(listOf("a", "ab", "b").groupingBy { it.first() }.eachCount())      // {a=2, b=1}
    println("Mississippi".groupingBy { it }.eachCount())                        // {M=1, i=4, s=4, p=2}
    println(listOf(1, 2, 3, 4, 5, 6).groupingBy { it % 3 }.eachCount())         // {1=2, 2=2, 0=2}
}
