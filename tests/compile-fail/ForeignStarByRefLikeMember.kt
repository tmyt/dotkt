import ForeignStarReflectionSignatureFixture.Box
import ForeignStarReflectionSignatureFixture.Factory

fun foreignStarByRefLikeMember() {
    val box: Box<*> = Factory.Create()
    println(box.SpanValue())
}
