import ForeignByRefLikeStarProjectionFixture.Cell
import ForeignByRefLikeStarProjectionFixture.Factory

fun foreignByRefLikeStarProjection() {
    val cell: Cell<*> = Factory.Create()
    println(cell.Value)
}
