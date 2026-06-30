// A small Kotlin LIBRARY, compiled to a .NET dll by DotKt and consumed by app/App.kt through a
// <ProjectReference>. It exercises the Kotlin surface a library realistically exposes — a class with a
// computed property + method, a data class (generated toString), an enum, a top-level function, and a
// top-level extension function — all of which must survive the trip into the emitted assembly (the
// [Kotlin*] round-trip metadata) and be resolvable when the app imports them AS KOTLIN.
package shapes

// Regular class: constructor-val properties, a computed (get-only) property, and a member function.
class Rectangle(val width: Int, val height: Int) {
    val area: Int get() = width * height
    fun scaled(factor: Int): Rectangle = Rectangle(width * factor, height * factor)
}

// Data class: the compiler-generated toString()/equals() ride along into the dll.
data class Point(val x: Int, val y: Int)

// Enum class.
enum class Color { RED, GREEN, BLUE }

// Top-level function (restored from the file-class [KotlinFileClass] metadata).
fun describe(r: Rectangle): String = "Rectangle " + r.width + "x" + r.height + " area=" + r.area

// Top-level EXTENSION function (needs the [KotlinFunction] round-trip metadata to re-import as `p.manhattan()`).
fun Point.manhattan(): Int = (if (x < 0) -x else x) + (if (y < 0) -y else y)
