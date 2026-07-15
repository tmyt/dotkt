// The APP references mylib (../lib/Lib.ktproj) via <ProjectReference> and consumes its collection-typed signatures
// AS KOTLIN. The lib's emitted dll exposes them as BCL interfaces (IReadOnlyList/IList/IDictionary); facadegen's
// reverse map (#27) surfaces them back as kotlin.collections.* so these listOf/mutableListOf/mapOf calls resolve.
import mylib.takesList
import mylib.takesMutable
import mylib.takesMap
import mylib.makeHolder

fun main() {
    println(takesList(listOf("a", "b")))              // 2   (List<String> param + listOf)
    println(takesMutable(mutableListOf(1, 2)))        // 3   (MutableList<Int> param + mutableListOf; lib adds 99)
    println(takesMap(mapOf("x" to 10, "y" to 20)))    // 2   (Map<String,Int> param + mapOf)
    val h = makeHolder(listOf("x", "y"))              //     (generic inference: T = String)
    println(h.items.size)                             // 2   (List<T> property member resolution)
}
