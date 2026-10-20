package roundtrip.annotationproperties

annotation class ImportedLabel(val value: String, val code: Int = 11)
annotation class ImportedArrays(val names: Array<String>, val counts: IntArray)
annotation class ImportedTags(vararg val tags: String)
annotation class ImportedTypes(val nested: ImportedLabel, val kind: DeprecationLevel)

fun producedLabel(): ImportedLabel = ImportedLabel("producer", 17)
fun producedArrays(): ImportedArrays = ImportedArrays(arrayOf("one", "two"), intArrayOf(7, 9))
fun producedTags(): ImportedTags = ImportedTags("one", "two")
fun producedTypes(): ImportedTypes = ImportedTypes(ImportedLabel("nested"), DeprecationLevel.WARNING)
