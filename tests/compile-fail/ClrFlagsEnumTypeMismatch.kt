import ClrFlagsEnumTypeMismatchFixture.FirstFlags
import ClrFlagsEnumTypeMismatchFixture.SecondFlags

fun mixedFlagsEnumTypes() {
    println(FirstFlags.First or SecondFlags.Second)
}
