// B4 (kotlin.time) gate: companion extension-property accessors carry their receiver
// (2.seconds -> Duration.get_seconds(int __self)), and value-class member operators emit as REAL
// method calls (Duration.plus/unaryMinus), not raw CIL add/neg. Duration.toString exercises the
// inline-splice early-return + member-inline dispatch-receiver bindings (indexOfLast/toComponents).
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

fun main() {
    val d = 2.seconds + 3.seconds
    println(d)                                     // 5s
    println(1500.milliseconds + 500.milliseconds)  // 2s   (carry across units)
    println(-(1.seconds))                          // -1s  (unaryMinus + negative toString)
    println((2.seconds - 3.seconds).isNegative())  // True
}
