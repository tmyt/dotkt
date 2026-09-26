package roundtriptests.genericnullablecallbacks

import NUnit.Framework.TestAttribute
import roundtrip.genericnullablecallbacks.*

class GenericNullableCallbacksTests {
    @TestAttribute
    fun localCallbacksUseDeclaredPhysicalFrames() {
        verifyLocalNullableCallbacks()
    }

    @TestAttribute
    fun importedCallbacksUseDeclaredPhysicalFrames() {
        check(identityCallback<Int>()(null) == null)
        check(identityCallback<Int>()(42) == 42)
        val local = identityCallback<Int>()
        check(local(null) == null && local(7) == 7)
        check(identityCallback<String>()(null) == null)
        check(identityCallback<String>()("text") == "text")
        check(forwardedCallback<Int>()(19) == 19)
        check(capturedCallback<Int>(23)(null) == 23)
        check(capturedCallback<Int>(23)(31) == 31)
        check(invokeCallback<Int>({ it }, null) == null)
        check(invokeCallback<Int>({ it }, 37) == 37)
        check(callbackHolder<Int>().callback(null) == null)
        check(callbackHolder<Int>().callback(41) == 41)
        check(forwardedCallback<String>()("forward") == "forward")
        check(capturedCallback<String>("fallback")(null) == "fallback")
        check(invokeCallback<String>({ it }, "argument") == "argument")
        check(callbackHolder<String>().callback("holder") == "holder")
        check(extensionCallback<Int>()(null) == null)
        check(extensionCallback<Int>()(43) == 43)
        check(firstCallback<Int, String>()(47, null) == 47)
        check(firstCallback<String, Int>()(null, 53) == null)
        check(nestedCallback<Int>()()(59) == 59)
        var observed: Int? = null
        val action = unitCallback<Int> { observed = it }
        action(61)
        check(observed == 61)
        action(null)
        check(observed == null)
        val function = identityCallback<Int>()
        val alias = function
        check(function === alias && function == alias)
    }
}
