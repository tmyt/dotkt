// Producer surface part 2: a data class with an operator fun (+) and an infix fun. operator/infix are
// Kotlin-only call-shape modifiers with no .NET analog — they ride across as DotKt.Metadata and the consumer
// must be able to call `a + b` / `a scaledBy n` (not just `a.plus(b)` / `a.scaledBy(n)`).
package roundtrip.api

data class Money(val cents: Int) {
    operator fun plus(other: Money): Money = Money(cents + other.cents)
    infix fun scaledBy(factor: Int): Money = Money(cents * factor)
}
