package interop.nativenestedgenerics

import NUnit.Framework.TestAttribute
import NativeNestedGenerics.Api
import NativeNestedGenerics.Box
import System.Tuple2
import kotlin.clr.byref

private fun <T> nestedNativeBox(value: T): Box<Box<T>> = Box(Box(value))

class NativeNestedGenericTests {
    @TestAttribute
    fun nativeDefinedGenericReferencesPreserveAliasing() {
        var box = Box("initial")
        val initial = box
        check(Api.ReplaceAliased(byref(box), byref(box)))
        check(box.Value == "second")
        check(initial.Value == "initial")
        check(box !== initial)
    }

    @TestAttribute
    fun nativeDefinedGenericReferencesShareCapturedCallbackStorage() {
        var box = Box("initial")
        val result = Api.ReplaceCallback(byref(box), System.Action {
            check(box.Value == "native")
            box = Box("callback")
        })
        check(result === box)
        check(box.Value == "callback")
    }

    @TestAttribute
    fun nativeNestedConstructionPreservesExactTypeAndIdentity() {
        val inner = Box("hello")
        val box = Box(inner)
        check(Api.ExactBox(box))
        check(Api.EchoBox(box) === box)
        check(Api.EchoBox(box).Value === inner)
        val replacement = Box("replacement")
        box.Value = replacement
        check(Api.EchoBox(box).Value === replacement)
    }

    @TestAttribute
    fun nativeNestedConstructionPreservesGenericFrames() {
        val strings = nestedNativeBox("generic")
        check(Api.ExactBox(strings))
        check(Api.EchoGeneric(strings) === strings)
        check(Api.EchoBox(strings).Value.Value == "generic")
        val integers = nestedNativeBox(42)
        check(Api.EchoGeneric(integers) === integers)
        check(Api.EchoGeneric(integers).Value.Value == 42)
    }

    @TestAttribute
    fun frameworkNestedTuplePreservesExactTypeAndIdentity() {
        val tuple = Tuple2<Tuple2<String, String>, String>(
            Tuple2<String, String>("left", "right"), "outer")
        check(Api.ExactTuple(tuple))
        check(Api.EchoTuple(tuple) === tuple)
        check(Api.EchoTuple(tuple).Item1.Item1 == "left")
        check(Api.EchoTuple(tuple).Item1.Item2 == "right")
        check(Api.EchoTuple(tuple).Item2 == "outer")
        val fromNative = Api.MakeTuple()
        check(Api.EchoTuple(fromNative) === fromNative)
        check(fromNative.Item1.Item1 == "native-left")
    }
}
