package roundtrip.collectionarrayidentity

fun collectionArrayIdentity(values: Array<List<String>>): Array<List<String>> = values
fun collectionVarargSize(vararg values: List<String>): Int = values.size
class CollectionArrays(var values: Array<List<String>>)
