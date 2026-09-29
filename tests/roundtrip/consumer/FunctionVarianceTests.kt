package roundtriptests.functionvariance

import NUnit.Framework.TestAttribute
import roundtrip.functionvariance.*

class FunctionVarianceTests {
    @TestAttribute
    fun sourceSignaturesAndIdentitySurviveDllBoundaries() {
        val next: (Int) -> Int = increment()
        check(next(4) == 5)
        val retained: (Int) -> Int = keep(next)
        check(retained === next)
        val holder = FunctionHolder(next)
        val property: (Int) -> Int = holder.callback
        check(property === next)
        check(property(6) == 7)
        check(invokeDefault(9) == 10)
        check(optional(false) == null)
        val maybe: ((String?) -> String?)? = optional(true)
        check(maybe!!(null) == null)
        check(maybe("text") == "text")
        val outer: (String?) -> ((Int) -> String?) = nested()
        check(outer("nested")(1) == "nested")
        check(outer("nested")(0) == null)
        val receiver: String?.(Int) -> String? = extension()
        check(receiver("receiver", 1) == "receiver")
        check(receiver(null, 1) == null)
        val contextFunction: context(Context) (Int) -> String? = contextual()
        with(Context("context")) { check(contextFunction(1) == "context") }
        check(boxed().value(10) == 11)
        check(inlineIncrement(20) == 21)
        check(inlineIncrement(30, next) == 31)
    }
}
