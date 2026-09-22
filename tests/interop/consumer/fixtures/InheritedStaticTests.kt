import NUnit.Framework.TestAttribute
import InheritedStatics.*

private class StaticKotlinLeaf : Base()
private class StaticKotlinDeepLeaf : Middle()
private class StaticKotlinStringLeaf : GenericBase<String>()
private class StaticKotlinIntLeaf : GenericBase<Int>()
private class StaticKotlinPairLeaf : Swap<Int, String>()
private typealias StaticAliasLeaf = StringLeaf
private typealias StaticAliasBase = Base

class InheritedStaticTests {
    @TestAttribute fun sameNameAssignmentsKeepLeftAndRightOwnersSeparate() {
        StringLeaf.Property = "generic"
        Base.Property = "base"
        Base.Property = StringLeaf.Property
        check(Base.Property == "generic" && Storage.StringProperty() == "generic")
        Leaf.Property = "leaf"
        StringLeaf.Property = Leaf.Property
        check(Base.Property == "leaf" && Storage.StringProperty() == "leaf")
        StringLeaf.Property = "second"
        Leaf.Property = StringLeaf.Property
        check(Base.Property == "second" && Storage.StringProperty() == "second")
    }

    @TestAttribute fun propertyAndFieldReferencesKeepConstructedOwners() {
        val property = StringLeaf::Property
        property.set("property reference")
        check(property.get() == "property reference" && Storage.StringProperty() == "property reference")
        val field = StaticKotlinIntLeaf::Field
        field.set(79)
        check(field.get() == 79 && Storage.IntField() == 79)
        check(Storage.ObjectUntouched())
    }

    @TestAttribute fun defaultsKeepTheirDeclaringSourceFile() {
        StringLeaf.Property = "property default"
        check(inheritedStaticPropertyDefault() == "property default")
        check(inheritedStaticMethodDefault() == "method default")
        check(Storage.StringField() == "method default" && Storage.ObjectUntouched())
    }

    @TestAttribute fun typealiasCanNameTheDeclaringClassItself() {
        StaticAliasBase.Field = "direct alias"
        check(Base.Field == "direct alias" && StaticAliasBase.Method() == "base method")
    }

    @TestAttribute fun inheritedStaticOverloadsRemainAvailable() {
        check(Leaf.Over(83) == "base overload")
        check(Leaf.Over("text") == "leaf overload")
    }

    @TestAttribute fun typealiasRetainsConstructedDeclarationOwner() {
        StaticAliasLeaf.Field = "alias"
        check(Storage.StringField() == "alias")
        check(StaticAliasLeaf.Store("method") == "method" && Storage.StringField() == "method")
        check(Leaf.Readonly == 59 && StaticKotlinLeaf.Readonly == 59)
    }

    @TestAttribute fun callableReferenceRetainsConstructedDeclarationOwner() {
        val foreign: (String) -> String = StringLeaf::Store
        val kotlin: (Int) -> Int = StaticKotlinIntLeaf::Store
        check(foreign("reference") == "reference" && Storage.StringField() == "reference")
        check(kotlin(67) == 67 && Storage.IntField() == 67)
        check(Storage.ObjectUntouched())
    }

    @TestAttribute fun inheritedStaticEventUsesBaseSubscription() {
        var total = 0
        val token = Leaf.Changed.subscribe { value -> total += value }
        try {
            StaticKotlinLeaf.Raise(71)
            check(total == 71)
        } finally {
            token.close()
        }
        Base.Raise(73)
        check(total == 71)
    }

    @TestAttribute fun foreignAndKotlinSubclassesShareBaseStorage() {
        Leaf.Field = "field"
        StaticKotlinLeaf.Property = "property"
        check(Base.Field == "field" && StaticKotlinLeaf.Field == "field")
        check(Base.Property == "property" && Leaf.Property == "property")
        check(Leaf.Method() == "base method" && StaticKotlinLeaf.Method() == "base method")
    }

    @TestAttribute fun nearestStaticDeclarationHidesBase() {
        Base.Field = "base"
        DeepLeaf.Field = 47
        StaticKotlinDeepLeaf.Property = 53
        check(Middle.Field == 47 && StaticKotlinDeepLeaf.Field == 47)
        check(Middle.Property == 53 && DeepLeaf.Property == 53)
        check(Base.Field == "base")
        check(DeepLeaf.Method() == "middle method" && StaticKotlinDeepLeaf.Method() == "middle method")
    }

    @TestAttribute fun referenceArgumentSelectsConstructedStaticStorage() {
        StringLeaf.Field = "field"
        StaticKotlinStringLeaf.Property = "property"
        check(StaticKotlinStringLeaf.Field == "field" && StringLeaf.Property == "property")
        check(Storage.StringField() == "field" && Storage.StringProperty() == "property")
        check(StringLeaf.Store("foreign") == "foreign" && Storage.StringField() == "foreign")
        check(StaticKotlinStringLeaf.Store("kotlin") == "kotlin" && Storage.StringField() == "kotlin")
        check(Storage.ObjectUntouched())
    }

    @TestAttribute fun valueArgumentSelectsConstructedStaticStorage() {
        IntLeaf.Field = 17
        StaticKotlinIntLeaf.Property = 19
        check(StaticKotlinIntLeaf.Field == 17 && IntLeaf.Property == 19)
        check(Storage.IntField() == 17 && Storage.IntProperty() == 19)
        check(IntLeaf.Store(23) == 23 && Storage.IntField() == 23)
        check(StaticKotlinIntLeaf.Store(29) == 29 && Storage.IntField() == 29)
        check(Storage.ObjectUntouched())
    }

    @TestAttribute fun multilevelSubstitutionPreservesReorderedArguments() {
        PairLeaf.First = "first"
        StaticKotlinPairLeaf.Second = 31
        check(StaticKotlinPairLeaf.First == "first" && PairLeaf.Second == 31)
        check(Storage.PairFirst() == "first" && Storage.PairSecond() == 31)
    }

    @TestAttribute fun genericMethodKeepsBothOwnerAndMethodFrames() {
        check(PairLeaf.Store("foreign", 37, "result") == "result")
        check(Storage.PairFirst() == "foreign" && Storage.PairSecond() == 37)
        check(StaticKotlinPairLeaf.Store("kotlin", 41, "result") == "result")
        check(Storage.PairFirst() == "kotlin" && Storage.PairSecond() == 41)
    }
}
