package roundtrip.externalcompanionreceiver

import roundtrip.companionreceiver.NamedHost
import roundtrip.companionreceiver.DefaultHost
import roundtrip.companionreceiver.GenericHost

fun NamedHost.Named.externalSelected(value: Int): Int = value + 11
fun DefaultHost.Companion.externalSelected(value: Int): Int = value + 12
fun GenericHost.Named.externalSelected(value: Int): Int = value + 14
val NamedHost.Named.externalLabel: String get() = "external"
