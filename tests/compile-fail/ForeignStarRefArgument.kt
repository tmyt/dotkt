import ForeignStarReflectionSignatureFixture.Box
import ForeignStarReflectionSignatureFixture.Factory
import kotlin.clr.byref

fun foreignStarRefArgument() {
    val box: Box<*> = Factory.Create()
    var value = 1
    box.Replace(byref(value))
}
