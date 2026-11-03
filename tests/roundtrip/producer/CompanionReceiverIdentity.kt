package roundtrip.companionreceiver

class NamedHost { companion object Named }
class DefaultHost { companion object }
class OtherNamedHost { companion object Named }
class GenericHost<T> { companion object Named }
class OuterHost { class NestedHost { companion object } }

fun NamedHost.Named.selected(value: Int): Int = value + 1
fun DefaultHost.Companion.selected(value: Int): Int = value + 2
fun OtherNamedHost.Named.selected(value: Int): Int = value + 3
fun GenericHost.Named.selected(value: Int): Int = value + 4
fun OuterHost.NestedHost.Companion.selected(value: Int): Int = value + 5

val NamedHost.Named.label: String get() = "named"
fun <T> NamedHost.Named.keep(value: T): T = value
fun namedValue(): NamedHost.Named = NamedHost.Named
fun namedValues(): List<NamedHost.Named> = listOf(NamedHost.Named)

class CompanionBox(val value: NamedHost.Named)
