package roundtrip.sharedcellcarrier

class ImportedTagBox<T>(val tag: String)
fun importedRaw(): Any = ImportedTagBox<Int>("foreign")
