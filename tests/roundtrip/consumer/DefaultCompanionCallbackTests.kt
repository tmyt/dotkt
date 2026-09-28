import NUnit.Framework.TestAttribute
import roundtrip.defaultcompanion.DefaultCompanionOwner
import roundtrip.defaultcompanion.Segment
import roundtrip.defaultcompanion.sameModuleDefault

private fun <A, T> importedCompanionDefault(unused: A, item: T): T {
    val owner = DefaultCompanionOwner(Segment<T>(null))
    var calls = 0
    val result = owner.select(item, { calls += 1; item })
    check(calls == 1)
    return result
}

class DefaultCompanionCallbackTests {
    @TestAttribute
    fun unusedDefaultCallbackHasCompleteConstructedFrames() {
        check(sameModuleDefault(17) == 17)
        check(sameModuleDefault("text") == "text")
        check(sameModuleDefault<Int?>(null) == null)
        check(importedCompanionDefault("owner", 23) == 23)
        check(importedCompanionDefault(29, "method") == "method")
        check(importedCompanionDefault<String, Int?>("nullable", null) == null)
    }

    @TestAttribute
    fun invokedAndExplicitCallbacksKeepTheirGenericFrames() {
        val segment = Segment<Int>(null)
        val owner = DefaultCompanionOwner(segment)
        check(owner.invokeDefault(31, "result") == "result")
        check(segment.value == 31)
        check(owner.select(37, { 41 }, { _, _ -> error("not used") }) == 41)
        val nullable = Segment<String?>("before")
        check(DefaultCompanionOwner(nullable).invokeDefault(null, 43) == 43)
        check(nullable.value == null)
    }
}
