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
    }
}
