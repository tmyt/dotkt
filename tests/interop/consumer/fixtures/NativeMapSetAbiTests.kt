import NUnit.Framework.TestAttribute
import mapsetabi.NativeMaps

private fun <K, V> setNativeView(target: MutableMap<K, V>, key: K, value: V) { target[key] = value }

class NativeMapSetAbiTests {
    @TestAttribute
    fun mapSetUsesNativeStorageWithoutChangingTheClrBoundary() {
        val integers = NativeMaps.Integers()
        val names = integers as MutableMap<Int, String>
        setNativeView(names, 7, "first")
        names[7] = "second"
        check(NativeMaps.HasInteger(integers, 7, "second"))

        val durations = NativeMaps.Durations()
        setNativeView(durations as MutableMap<String, System.TimeSpan>, "duration", System.TimeSpan(0, 0, 5))
        check(NativeMaps.HasDuration(durations, "duration", 5.0))

        val objects = NativeMaps.Objects()
        val optional = objects as MutableMap<String, Int?>
        setNativeView(optional, "null-value", null)
        setNativeView(optional, "number", 7)
        check(NativeMaps.HasNull(objects, "null-value"))
        check(NativeMaps.HasBoxedInteger(objects, "number", 7))
    }
}
