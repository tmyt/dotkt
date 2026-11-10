package roundtriptests.internaldefaults

import NUnit.Framework.TestAttribute
import System.Reflection.BindingFlags
import roundtrip.internaldefaults.*

class InternalDefaultAccessTests {
    @TestAttribute
    fun movedDefaultsKeepInternalVisibilityAndDeclarationFrames() {
        val owner = InternalDefaultOwner(7)
        check(owner.method() == 7 && owner.reads == 1)
        check(owner.method(19) == 19 && owner.reads == 1)
        check(owner.reference() == 7 && owner.reads == 2)
        check(owner.property() == 7)
        owner.writeField(11)
        check(owner.field() == 11)
        check(owner.constructed().value == 7)
        check(owner.generic("method frame") == "method frame")
        check(owner.generic<String?>(null) == null)
        check(topDefault() == 42 && sameModuleInternalDefault() == 23)

        val nullable = InternalDefaultOwner<Int?>(null)
        check(nullable.method() == null && nullable.reference() == null)
        check(nullable.property() == null && nullable.field() == null && nullable.constructed().value == null)
        val marker = InternalDefaultMarker()
        val reference = InternalDefaultOwner(marker)
        check(reference.method() === marker && reference.reference() === marker)
        check(reference.property() === marker && reference.field() === marker && reference.constructed().value === marker)
        check(reference.generic(marker) === marker)

        val flags = BindingFlags.Instance or BindingFlags.Public or BindingFlags.NonPublic
        val type = (owner as System.Object).GetType()
        check(type.GetMethod("internalRead", flags)!!.IsAssembly)
        check(type.GetField("field", flags)!!.IsAssembly)
        check(type.GetProperty("property", flags)!!.GetGetMethod(true)!!.IsAssembly)
        val constructors = (owner.constructed() as System.Object).GetType().GetConstructors(flags)
        check(constructors.size == 1 && constructors[0].IsAssembly)
    }
}
