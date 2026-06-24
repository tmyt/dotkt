class User(val data: Map<String, Any?>) {
    val name: String by data
    val age: Int by data
}
fun main() {
    val u = User(mapOf("name" to "Alice", "age" to 30))
    println(u.name)
    println(u.age)
}
