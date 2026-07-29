// A NON-Comparable class carrying an explicit `operator fun compareTo` (a standalone comparable-by-value
// type, NOT `: Comparable<T>`): kotc keeps the LOWERCASE `compareTo` name and stamps the REAL operator flag,
// so `<`/`>` resolve across the reference KLIB purely from the [KotlinFunction] flag. Plus an
// infix restored the same way.
package roundtrip.money

class Money(val cents: Int) {
    operator fun compareTo(o: Money): Int = cents - o.cents   // real `operator` flag (kotc isOperator), NOT a name-hack
    infix fun combine(o: Money): Money = Money(cents + o.cents)
}
