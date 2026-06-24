// Phase 6 — the `sequence { yield(…) }` builder: a restricted-suspension (multi-shot) coroutine producing a lazy
// Sequence<T> -> .NET IEnumerable<T>. yield(v) is a suspension point; the block runs to the next yield per step.
fun main() {
    // Straight-line yields.
    val s = sequence {
        yield(1)
        yield(2)
        yield(3)
    }
    println(s.toList().joinToString(","))     // 1,2,3

    // A yield inside a loop (live locals survive across yields as state-machine fields).
    val squares = sequence {
        var i = 1
        while (i <= 4) {
            yield(i * i)
            i = i + 1
        }
    }
    println(squares.toList().joinToString(","))   // 1,4,9,16

    // Laziness: only the first 2 are forced (take short-circuits the lazy IEnumerable).
    val firstTwo = sequence {
        var i = 0
        while (true) {
            yield(i)
            i = i + 1
        }
    }.take(2).toList()
    println(firstTwo.joinToString(","))       // 0,1

    // yieldAll: splice every element of an Iterable into the sequence (an inner enumerator loop in the SM).
    val mixed = sequence {
        yield(0)
        yieldAll(listOf(1, 2, 3))
        yieldAll(sequence { yield(4); yield(5) })
        yield(6)
    }
    println(mixed.toList().joinToString(","))  // 0,1,2,3,4,5,6
}
