// Class-implements-interface assignability: a concrete injected class flows where its interface is expected.
import P.IShape
import P.Circle
import P.Square
import P.Drawer
fun main() {
    val d = Drawer()
    println(d.Draw(Circle()))        // draw:circle
    println(d.Draw(Square()))        // draw:square
    val s: IShape = Circle()         // upcast to the interface type
    println(s.Describe())            // circle
}
