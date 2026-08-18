package defaultprobe

import Probe.DefaultCarrier1
import Probe.DefaultCarrier2
import Probe.DefaultPropertyCarrier
import Probe.ConstructedDefaultCarrier
import Probe.ExternalDefaultCarrier
import Probe.ExplicitDefaultCarrier
import Probe.GenericDefaultCarrier
import Probe.IPublicDefaultSlot
import Probe.IPublicDefaultProperty
import Probe.IPublicGenericDefaultSlot
import Probe.IPublicNullabilityDefaultSlot
import Probe.NullabilityDefaultCarrier
import Probe.Contracts.IExternalDefaultSlot

class K1 : DefaultCarrier1()

class K2 : DefaultCarrier2()

class ConstructedK : ConstructedDefaultCarrier()

class GenericK : GenericDefaultCarrier()

class ExternalK : ExternalDefaultCarrier()

class ExplicitK : ExplicitDefaultCarrier()

class PropertyK : DefaultPropertyCarrier()

class NullabilityK : NullabilityDefaultCarrier()

fun main() {
    val first: IPublicDefaultSlot = K1()
    val second: IPublicDefaultSlot = K2()
    first.M()
    second.M()
    val constructed: IPublicDefaultSlot = ConstructedK()
    constructed.M()
    val generic: IPublicGenericDefaultSlot<String> = GenericK()
    val external: IExternalDefaultSlot = ExternalK()
    val explicit: IExternalDefaultSlot = ExplicitK()
    val property: IPublicDefaultProperty = PropertyK()
    val nullable: String? = (NullabilityK() as IPublicNullabilityDefaultSlot).Normalize("ok")
    println(generic.Echo("ok").length + external.Value() + explicit.Value() + property.Number + (nullable?.length ?: 0) - 2)
}
