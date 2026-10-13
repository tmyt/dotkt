package roundtriptests.aliases

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.aliases.*
import roundtrip.aliases.only.OnlyText
import roundtrip.actualalias.Cancellation as ActualCancellation
import roundtrip.actualalias.CommonCancellation

private fun isAlias(error: Throwable): Boolean = error is Cancellation
private fun isCanonical(error: Throwable): Boolean = error is kotlin.coroutines.cancellation.CancellationException
private fun isActualAlias(error: Throwable): Boolean = error is ActualCancellation
private fun isCommonAlias(error: Throwable): Boolean = error is CommonCancellation
private fun isArgumentError(error: Throwable): Boolean = error is IllegalArgumentException

class TypeAliasTests {
    @TestAttribute
    fun exceptionAliasRetainsRuntimeIdentityForTypeTests() {
        val error: Throwable = Cancellation("cancelled")
        check(isAlias(error))
        check(isCanonical(error))
        check(!isArgumentError(error))
    }

    @TestAttribute
    fun exceptionAliasCanCatchItsExpandedType() {
        var caught = false
        try {
            throw kotlin.coroutines.cancellation.CancellationException("stop")
        } catch (error: Cancellation) {
            caught = true
            check(error.message == "stop")
        }
        check(caught)
    }

    @TestAttribute
    fun actualAliasResolvesWithoutItsExpectClass() {
        val error: Throwable = ActualCancellation("actual")
        check(isActualAlias(error))
        check(isCommonAlias(error))
        check(isAlias(error))
        var caught = false
        try { throw error } catch (cancelled: ActualCancellation) { caught = true }
        check(caught)
    }

    @TestAttribute
    fun aliasOnlyPackageRetainsNullableAndChainedAliases() {
        val text: TextChain = "chain"
        val missing: NullableText = null
        val only: OnlyText = text
        check(text == "chain")
        check(missing == null)
        check(only == "chain")
    }

    @TestAttribute
    fun genericAndStarAliasesRetainTheirKotlinArguments() {
        val values: Values<String> = listOf("first", "second")
        val integers: Values<Int> = listOf(7, 11)
        val stars: AnyValues = integers
        check(values[1] == "second")
        check(integers[0] == 7)
        check(stars.size == 2)
    }

    @TestAttribute
    fun functionAliasRetainsItsExtensionReceiver() {
        val callback: Callback<String> = { suffix -> this + suffix }
        check(callback("left", "right") == "leftright")
    }

    @TestAttribute
    fun suspendFunctionAliasRetainsItsReceiverAndLogicalResult() {
        val callback: Suspended<String> = { suffix -> this + suffix }
        val block: suspend () -> String = { callback("left", "right") }
        var completedValue = ""
        var failure: Throwable? = null
        block.startCoroutine(object : Continuation<String> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<String>) {
                failure = result.exceptionOrNull()
                if (failure == null) completedValue = result.getOrThrow()
            }
        })
        val error = failure
        if (error != null) throw error
        check(completedValue == "leftright")
    }

    @TestAttribute
    fun nestedAliasesRetainTheirSemanticOwner() {
        val text: AliasOwner.Nested = "nested"
        val values: AliasOwner.Values<Int> = listOf(13)
        check(text == "nested")
        check(values[0] == 13)
    }
}
