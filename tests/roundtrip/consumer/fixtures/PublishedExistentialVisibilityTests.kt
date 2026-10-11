import NUnit.Framework.TestAttribute
import roundtrip.publishedexistential.publishedClass
import roundtrip.publishedexistential.publishedInterface

class PublishedExistentialVisibilityTests {
    @TestAttribute
    fun publishedClassCarrierIsCallableFromConsumerInlineBody() {
        check(publishedClass(17) { check(it == 17) } == "published-class")
        check(publishedClass("class") { check(it == "class") } == "published-class")
    }

    @TestAttribute
    fun publishedInterfaceCarrierIsCallableFromConsumerInlineBody() {
        check(publishedInterface(23) { check(it == 23) } == "published-interface")
        check(publishedInterface("interface") { check(it == "interface") } == "published-interface")
    }
}
