// The APP. It references the lib (../lib/Shapes.ktproj) via <ProjectReference> and consumes its
// declarations AS KOTLIN — the lib is built first, its emitted Shapes.dll is referenced as a real .NET
// assembly (not recompiled from source), and these imports resolve against that dll's round-trip metadata.
import shapes.Rectangle
import shapes.Point
import shapes.Color
import shapes.describe
import shapes.manhattan

fun main() {
    val r = Rectangle(3, 4)
    println(describe(r))            // Rectangle 3x4 area=12   (top-level fn + computed property, from the lib)
    println(r.scaled(2).area)      // 48                       (member fn returning the lib's own type)
    val p = Point(-2, 5)
    println(p)                     // Point(x=-2, y=5)         (data-class toString from the lib)
    println(p.manhattan())         // 7                        (top-level extension fn round-trip)
    println(Color.BLUE)            // BLUE                     (enum constant from the lib)
}
