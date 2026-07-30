// The DEFAULT-PACKAGE half of the `Nothing` round-trip (#135/#197). Deliberately carries NO `package`
// declaration — every other producer file has one, and the shell scenario this case replaced had BOTH of its
// producers in the default package. That is a distinct path, not a cosmetic difference: a default-package
// top-level function is attributed to a ROOT-namespace file class, and the consumer resolves it from the
// reference KLIB with no package qualifier at all. Without a default-package producer, a regression in that
// attribution (or in [KotlinNothing] restoration through it) would pass every gate silently.
//
// Names are `rtDefault`-prefixed because this file shares the root namespace with anything else that lands there.

fun rtDefaultFail(msg: String): Nothing = throw RuntimeException(msg)

class RtDefaultBoom {
    companion object { fun boom(): Nothing = throw RuntimeException("default-boom") }
}
