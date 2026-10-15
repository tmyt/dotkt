import roundtrip.publishedcallables.StaticHost
import roundtrip.publishedcallables.extensionProperty

fun main() { StaticHost.extensionProperty = 1; check(StaticHost.extensionProperty == 1) }
