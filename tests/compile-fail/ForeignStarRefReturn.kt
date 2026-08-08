import ForeignStarReflectionSignatureFixture.Box
import ForeignStarReflectionSignatureFixture.Factory

fun foreignStarRefReturn() {
    val box: Box<*> = Factory.Create()
    println(box.RefValue())
}
