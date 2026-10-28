import NUnit.Framework.TestAttribute
import roundtrip.mapsetabi.LibrarySetOwner
import roundtrip.mapsetabi.SetRecorder
import roundtrip.mapsetabi.storeFromLibrary
import roundtrip.mapsetabi.storeThen

class ImportedMapSetAbiTests {
    @TestAttribute
    fun importedSetWrappersKeepDispatchAndAliasedStorage() {
        val observed = SetRecorder<Int, String>()
        val alias: MutableMap<Int, String> = observed
        storeFromLibrary(alias, 7, "first")
        var callbacks = 0
        storeThen(alias, 7, "second") { check(it == "second"); callbacks++ }
        LibrarySetOwner<Int>().store(alias, 9, "third")
        check(callbacks == 1 && observed.writes == 3 && observed.size == 2)
        check(observed[7] == "second" && alias[9] == "third")

        val optional = SetRecorder<String?, Int?>()
        storeThen(optional, null, null) { check(it == null); callbacks++ }
        storeFromLibrary(optional, null, 7)
        LibrarySetOwner<String?>().store(optional, "null-value", null)
        check(callbacks == 2 && optional.writes == 3 && optional[null] == 7)
        check(optional.containsKey("null-value") && optional["null-value"] == null)

        val durations = SetRecorder<String, System.TimeSpan>()
        storeThen(durations, "duration", System.TimeSpan(0, 0, 5)) { check(it.TotalSeconds == 5.0) }
        check(durations.writes == 1 && durations["duration"]!!.TotalSeconds == 5.0)
        val nested = SetRecorder<List<Int>, Pair<String, Int?>>()
        val value: Pair<String, Int?> = "stored" to null
        storeThen(nested, listOf(1, 2), value) { check(it === value) }
        check(nested[listOf(1, 2)] === value && nested.writes == 1)
    }
}
