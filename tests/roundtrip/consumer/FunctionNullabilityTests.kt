package roundtriptests.functionnullability

import NUnit.Framework.TestAttribute
import roundtrip.functionnullability.*

class FunctionNullabilityTests {
    @TestAttribute
    fun localFunctionSurfacesRetainNullability() {
        verifyLocalFunctionNullability()
    }

    @TestAttribute
    fun importedFunctionSurfacesRetainNullability() {
        check(predicate()(null))
        check(!predicate()("value"))
        check(identity()(null) == null)
        val value: Int? = 42
        check(identity()(value) as Int == 42)
        check(optional(false) == null)
        val callback: (String?) -> String? = optional(true)!!
        check(callback(null) == null && callback("text") == "text")
        check(nested()(null)(null) == null)
        check(nested()("outer")(null) == "outer")
        check(genericIdentity<String>()(null) == null)
        check(genericIdentity<Int>()(null) == null)
        check(genericIdentity<Int>()(42) == 42)
        check(extension()(null, null) == null)
        check(extension()("receiver", null) == "receiver")
        with(Context(null)) { check(contextual()(null) == null) }
        with(Context("context")) { check(contextual()(null) == "context") }
        check(boxed().value(null) == null)
        val holder = Callbacks(identity(), optional(true))
        check(holder.callback(null) == null)
        check(holder.optional!!(null) == null)
        holder.callback = { if (it == null) "set" else it }
        check(holder.callback(null) == "set")
        val generic = GenericCallbacks<String>(genericIdentity())
        check(generic.callback(null) == null && generic.callback("value") == "value")
    }
}
