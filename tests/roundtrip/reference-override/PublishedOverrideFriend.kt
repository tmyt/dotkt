package roundtrip.referenceoverride

import roundtrip.publishedoverrides.Base
import roundtrip.publishedoverrides.ordinaryHook

class FriendDerived : Base<String>() { override fun hook(): Int = 21 }
fun main() { check(ordinaryHook(FriendDerived()) == 21) }
