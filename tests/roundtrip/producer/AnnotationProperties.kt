package roundtrip.annotationproperties

annotation class ImportedLabel(val value: String, val code: Int = 11)
annotation class ImportedArrays(val names: Array<String>, val counts: IntArray)

fun producedLabel(): ImportedLabel = ImportedLabel("producer", 17)
fun producedArrays(): ImportedArrays = ImportedArrays(arrayOf("one", "two"), intArrayOf(7, 9))
