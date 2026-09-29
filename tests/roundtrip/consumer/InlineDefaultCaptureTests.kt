import NUnit.Framework.TestAttribute
import roundtrip.defaultcapture.*

class InlineDefaultCaptureTests {
    @TestAttribute
    fun defaultOnlyCaptureRemainsDeferred() {
        var effects = 0
        val callback = deferredValue({}, "result", { effects++ })
        check(effects == 0)
        check(callback() == "result")
        check(callback() == "result")
        check(effects == 2)
        val same = sameModuleDeferred { effects++ }
        check(effects == 2)
        check(same() == "same")
        check(effects == 3)
    }

    @TestAttribute
    fun defaultsAndBodyShareOneFunctionValue() {
        var effects = 0
        val callback = sharedDefault({}, { effects++ })
        check(effects == 1)
        callback()
        check(effects == 3)
    }

    @TestAttribute
    fun defaultEvaluationFollowsSuppliedValuesAndPrecedesBody() {
        var trace = ""
        var count = 0
        fun value(): () -> Int {
            trace += "supplied;"
            return { trace += "default;"; ++count }
        }
        val result = "prefix:".orderedDefault(
            { trace += "body;" }, value())
        check(result == "prefix:1:2")
        check(trace == "supplied;default;default;body;")
        check(count == 2)
        val literal = "literal:".orderedDefault({}, { ++count })
        check(literal == "literal:3:4")
    }

    @TestAttribute
    fun explicitCallbacksDoNotInvokeUnusedEffects() {
        var effects = 0
        val callback = deferredValue({}, 42, { effects++ }, { 7 })
        check(callback() == 7)
        check(effects == 0)
        val nullable = deferredValue({}, null as String?, { effects++ })
        check(nullable() == null)
        check(effects == 1)
    }
}
