import ClrFlagsEnumTypeMismatchFixture.FirstFlags

fun plainEnumHasNoBitwiseSurface() {
    println(System.DayOfWeek.Monday or System.DayOfWeek.Tuesday)
}
