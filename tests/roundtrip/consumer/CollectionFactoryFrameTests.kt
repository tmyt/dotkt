import NUnit.Framework.TestAttribute
import factoryframes.FactoryFrameOwner

class CollectionFactoryFrameTests {
    @TestAttribute fun classGenericListFactoryKeepsItsCallerFrame() {
        check(FactoryFrameOwner("public").list.value[0] == "public")
        check(FactoryFrameOwner(7).list.value[0] == 7)
    }

    @TestAttribute fun varargListFactoryKeepsItsCallerFrame() {
        val values = FactoryFrameOwner(7).many.value
        check(values.size == 2 && values[0] == 7 && values[1] == 7)
        check(FactoryFrameOwner("a").many.value[1] == "a")
    }

    @TestAttribute fun setFactoryKeepsItsCallerFrame() {
        check(FactoryFrameOwner(7).set.value.contains(7))
        check(FactoryFrameOwner("a").set.value.contains("a"))
    }

    @TestAttribute fun mapFactoryKeepsBothUseSiteArguments() {
        check(FactoryFrameOwner(7).map.value["seed"] == 7)
        check(FactoryFrameOwner("a").map.value["seed"] == "a")
    }

    @TestAttribute fun ownerAndMethodGenericFramesStayDistinct() {
        check(FactoryFrameOwner("key").mixed(7).value["key"] == 7)
        check(FactoryFrameOwner(7).mixed("value").value[7] == "value")
    }

    @TestAttribute fun nullableFactoryArgumentsKeepTheirRepresentation() {
        val values = FactoryFrameOwner(7)
        check(values.nullable(9).value[0] == 9)
        check(values.nullable(null).value[0] == null)
        check(FactoryFrameOwner("a").nullable("b").value[0] == "b")
        check(FactoryFrameOwner("a").nullable(null).value[0] == null)
        check(FactoryFrameOwner<Int?>(null).list.value[0] == null)
    }

    @TestAttribute fun arrayElementsKeepTheirNestedGenericFrameAndIdentity() {
        val values = arrayOf(7)
        val stored = FactoryFrameOwner(7).arrays(values).value[0]
        check(stored === values && stored[0] == 7)
        check(FactoryFrameOwner("a").arrays(arrayOf("b")).value[0][0] == "b")
    }

    @TestAttribute fun functionElementsKeepTheirParameterFrame() {
        check(FactoryFrameOwner(7).functions { "v=$it" }.value[0](9) == "v=9")
        check(FactoryFrameOwner("a").functions { "v=$it" }.value[0]("b") == "v=b")
    }

    @TestAttribute fun constructorArgumentPermutationDoesNotRebindCallerVariables() {
        val result = FactoryFrameOwner("owner").reversed(9)
        check(result.first[0] == 9 && result.second[0] == "owner")
    }

    @TestAttribute fun mutableAndEmptyFactoriesKeepTheirUseSiteType() {
        val values = FactoryFrameOwner(7).mutable().value
        values.add(9)
        check(values[0] == 7 && values[1] == 9)
        check(FactoryFrameOwner(7).empty().value.isEmpty())
        check(FactoryFrameOwner("a").empty().value.isEmpty())
    }
}
