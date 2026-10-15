import roundtrip.publishedcallables.StaticHost

fun main() { StaticHost.staticProperty = 1; check(StaticHost.staticProperty == 1) }
