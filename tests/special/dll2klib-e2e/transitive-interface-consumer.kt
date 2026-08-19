package transitiveprobe

import TransitiveSlotProbe.Carrier
import TransitiveSlotProbe.IPublic
import TransitiveSlotProbe.GenericCarrier
import TransitiveSlotProbe.IPublicGeneric
import TransitiveSlotProbe.ExplicitCarrier
import TransitiveSlotProbe.IPublicExplicit
import TransitiveSlotProbe.PublicDerivedCarrier

class K : Carrier()
class GenericK : GenericCarrier()
class ExplicitK : ExplicitCarrier()
class PublicDerivedK : PublicDerivedCarrier()

fun consume(value: K): IPublic = value
fun consumeGeneric(value: GenericK): IPublicGeneric<String> = value
fun consumeExplicit(value: ExplicitK): IPublicExplicit = value
fun callExplicit(value: ExplicitK): Int = (value as IPublicExplicit).ReadExplicit()
fun consumePublicDerived(value: PublicDerivedK): IPublicExplicit = value
fun callPublicDerived(value: PublicDerivedK): Int = (value as IPublicExplicit).ReadExplicit()
