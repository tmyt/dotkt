package roundtrip.propertytypes

class PropertyHolder {
    var text: String? = "initial"
    val block: suspend () -> Int = { 7 }
    val extension: suspend Int.() -> Int = { this + 1 }
}
