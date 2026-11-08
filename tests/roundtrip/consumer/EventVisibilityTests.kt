package roundtriptests.eventvisibility

import NUnit.Framework.TestAttribute
import System.Reflection.BindingFlags
import roundtrip.eventvisibility.EventVisibilitySource
import roundtrip.eventvisibility.InheritedDefaultVisibilitySource
import roundtrip.eventvisibility.PolicyVisibilitySource

private class DerivedVisibilitySource : EventVisibilitySource<Int>() {
    fun familyHandle() = Family
}

class EventVisibilityTests {
    @TestAttribute
    fun nativeAccessorsRetainSourceVisibilityAndPublishedApiPromotion() {
        val type = (EventVisibilitySource<Int>() as System.Object).GetType()
        val flags = BindingFlags.Instance or BindingFlags.Public or BindingFlags.NonPublic
        for (name in listOf("Secret", "Family", "Assembly", "Published", "Visible")) {
            val event = type.GetEvent(name, flags)!!
            val methods = arrayOf(event.GetAddMethod(true)!!, event.GetRemoveMethod(true)!!, event.GetRaiseMethod(true)!!)
            for (method in methods) {
                check(when (name) {
                    "Secret" -> method.IsPrivate
                    "Family" -> method.IsFamily
                    "Assembly" -> method.IsAssembly
                    else -> method.IsPublic
                }) { "Wrong native visibility: $name" }
            }
            check((type.GetEvent(name) != null) == (name == "Published" || name == "Visible"))
        }
    }

    @TestAttribute
    fun restrictedRetainedHandlesAndMovedDefaultsKeepExactGenericAccess() {
        val source = DerivedVisibilitySource()
        val handles = listOf(source.secretHandle(), source.familyHandle(), source.assemblyHandle(),
            source.publishedHandle(), source.Visible, source.defaultHandle(), source.internalDefaultHandle())
        var sum = 0
        val tokens = handles.map { event -> event.subscribe(System.Action1<Int> { sum += it }) }
        source.fire(7)
        check(sum == 49)
        for (token in tokens) token.close()
        source.fire(99)
        check(sum == 49)

        val inherited = InheritedDefaultVisibilitySource()
        val inheritedToken = inherited.defaultFamilyHandle().subscribe { sum += it }
        inherited.fire(3)
        inheritedToken.close()
        inherited.fire(99)
        check(sum == 52)

        val custom = PolicyVisibilitySource()
        val token = custom.handle().subscribe { sum += it }
        custom.fire(8)
        token.close()
        custom.fire(99)
        check(sum == 60)
        val type = PolicyVisibilitySource::class as System.Type
        check(type.GetEvent("Secret") == null)
    }
}
