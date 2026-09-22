import NUnit.Framework.TestAttribute
import ProtectedStatics.*

private class ProtectedStaticChild : Base() {
    companion {
        val staticReference: (Int) -> Int = Base::Method
    }
    fun exercise() {
        Base.Field = 73
        ProtectedStaticChild.Property = 79
        check(Base.Field == 73 && ProtectedStaticChild.Field == 73)
        check(Base.ReadField() == 73 && Base.ReadProperty() == 79)
        check(Base.Method(81) == 83 && ProtectedStaticChild.Method(87) == 89)
        Base.Restricted = 97
        check(ProtectedStaticChild.Restricted == 97)
    }
    fun referencesAndLambda() {
        val method = Base::Method
        val field = ProtectedStaticChild::Field
        val property = Base::Property
        field.set(131)
        property.set(137)
        val read = { Base.Field + ProtectedStaticChild.Property }
        check(read() == 268 && method(137) == 139)
    }
    class Nested {
        fun read(): Int = Base.Method(147)
    }
}
private class ProtectedStaticMiddleChild : Middle() {
    fun exercise() {
        check(ProtectedStaticMiddleChild.Visible == 103)
        check(ProtectedStaticMiddleChild.VisibleProperty == 113)
        check(ProtectedStaticMiddleChild.VisibleMethod(3) == 130)
    }
}
private class ProtectedStaticStringChild : StringLeaf() {
    fun exercise() {
        ProtectedStaticStringChild.Value = "field"
        check(StringLeaf.Read() == "field")
        check(ProtectedStaticStringChild.Store("method") == "method")
        check(StringLeaf.Read() == "method")
        check(ProtectedStaticObjectChild.Read() == null)
    }
}
private class ProtectedStaticObjectChild : GenericBase<Any>()
private class ProtectedStaticGenericChild<T> : GenericBase<T>() {
    fun roundtrip(value: T): T {
        val store: (T) -> T = ProtectedStaticGenericChild<T>::Store
        return store(value)
    }
}

class ProtectedStaticTests {
    @TestAttribute fun derivedClassCanAccessProtectedStatics() { ProtectedStaticChild().exercise() }
    @TestAttribute fun protectedStaticsInReferencesLambdasAndNestedClasses() {
        ProtectedStaticChild().referencesAndLambda()
        check(ProtectedStaticChild.Nested().read() == 149)
        check(ProtectedStaticChild.staticReference(165) == 167)
    }
    @TestAttribute fun genericProtectedStaticsKeepConstructedOwner() {
        ProtectedStaticStringChild().exercise()
        check(ProtectedStaticGenericChild<String>().roundtrip("generic reference") == "generic reference")
        check(ProtectedStaticGenericChild<Int>().roundtrip(151) == 151)
    }
    @TestAttribute fun staticHidingUsesAccessSiteVisibility() {
        check(Leaf.Visible == 101 && Reader.Field() == 101)
        check(Leaf.VisibleProperty == 107 && Reader.Property() == 107)
        check(Leaf.VisibleMethod(3) == 112 && Reader.Method(3) == 112)
        ProtectedStaticMiddleChild().exercise()
        check(Leaf.Pick() == 157 && Reader.OptionalCall() == 157)
        check(Leaf.Pick(value = 5) == 162 && Reader.NamedCall() == 162)
        check(Leaf.Pick(5) == 168)
    }
}
