// I4 remnants, façade-free (import + facadegen injection):
//   - a .NET ENUM imports as an object of enum-typed vals: read, pass, compare (==), and `when` over it.
//   - GENERIC DELEGATES (BCL Func<int,int> and a custom Mapper<T>) take Kotlin lambdas.
//   - NULLABLE VALUE TYPES (`int?`/`double?`) map to Kotlin `Int?`/`Double?` both directions
//     (a plain Int and a null both convert into an `int?` param).
import I4.Probe
import I4.Color
import I4.GenDel

fun describe(c: Color): String = when (c) {
    Color.Red -> "warm"
    Color.Green -> "fresh"
    else -> "cool"
}

fun main() {
    val p = Probe()
    // enum
    val c = p.First()
    println(p.NameOf(c))                    // Green
    println(p.Code(Color.Blue))             // 4
    println(c == Color.Green)               // True
    println(describe(c))                    // fresh
    println(describe(Color.Blue))           // cool
    // generic delegates
    println(p.Apply({ x -> x + 5 }, 10))    // 15
    println(GenDel().Run({ v -> v * 3 }, 2)) // 18
    // nullable value types
    println(p.OrZero(p.MaybeVal(true)))     // 42
    println(p.OrZero(p.MaybeVal(false)))    // 0
    println(p.OrZero(7))                    // 7
    println(p.OrZero(null))                 // 0
    println(p.Half(3.0))                    // 1.5
}
