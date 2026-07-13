fun main() {
    val c = Cfg()
    // cross-file user-class property receiver -> CharSequence.split
    for (line in c.body.split("\n")) println("L:$line")
    // cross-file top-level fun result receiver -> CharSequence.split
    for (p in banner().split("-")) println("P:$p")
}
