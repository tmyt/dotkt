import roundtrip.publishedcallables.StaticHost
import roundtrip.publishedcallables.extensionHelper

fun main() { check(StaticHost.extensionHelper() == 7) }
