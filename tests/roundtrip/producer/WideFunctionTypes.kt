package roundtrip.wide

// The cross-module contract needs one representative arity. The ordinary basic compilation surface covers every
// KFunc/KAction declaration from 17 through 22; this file proves that a producer and consumer share the stdlib's
// nominal delegate identity after dll2klib re-import, in both parameter and return positions.
fun param17(f: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int): Int =
    f(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)

fun ret17(): (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int =
    { p1, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, p17 -> p1 + p17 }

fun retAction17(sink: (Int) -> Unit): (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Unit =
    { p1, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, p17 -> sink(p1 + p17) }

fun acceptWidened(f: (String, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Any): Any =
    f("s", 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)

fun narrowSource(): (Any, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> String =
    { value, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, p17 -> value.toString() + p17 }
