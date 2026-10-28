import NUnit.Framework.TestAttribute
import ArrayConversionInterop.Api

class NativeCollectionArrayConversionTests {
    @TestAttribute
    fun genericOnlyNativeCollectionsProduceExactArraysWithOneEnumeration() {
        val integers = Api.Integers()
        val integerView = integers as Collection<Int>
        val numbers = integerView.toTypedArray()
        check(Api.SumIntegers(numbers) == 16 && integers.Enumerations == 1)
        check(Api.Component(numbers) == "System.Int32")
        val widened: Collection<Any?> = integerView
        val objects = widened.toTypedArray()
        check(objects[0] == 7 && objects[1] == 9 && integers.Enumerations == 2)
        check(Api.Component(objects) == "System.Object")
        val strings = Api.Names()
        val names = (strings as Collection<String>).toTypedArray()
        check(Api.FirstName(names) == "first" && names[1] == "second" && strings.Enumerations == 1)
        val nullable = Api.NullableIntegers()
        val boxed = (nullable as Collection<Int?>).toTypedArray()
        check(boxed[0] == null && boxed[1] == 7 && nullable.Enumerations == 1)
        check(Api.Component(boxed) == "System.Object")
        val durations = Api.Durations()
        val values = (durations as Collection<System.TimeSpan>).toTypedArray()
        check(Api.FirstDuration(values) == 5.0 && durations.Enumerations == 1)
        check(Api.Component(values) == "System.TimeSpan")
    }
}
