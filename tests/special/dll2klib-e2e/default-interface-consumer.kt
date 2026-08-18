package defaultprobe

import Probe.DefaultCarrier1
import Probe.DefaultCarrier2
import Probe.DefaultPropertyCarrier
import Probe.DefaultIndexerCarrier
import Probe.DefaultEventCarrier
import Probe.ConstructedDefaultCarrier
import Probe.ExternalDefaultCarrier
import Probe.ExplicitDefaultCarrier
import Probe.ExplicitIndexerCarrier
import Probe.GenericDefaultCarrier
import Probe.IPublicDefaultSlot
import Probe.IPublicDefaultProperty
import Probe.IPublicDefaultIndexerSlot
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

class IndexerK : DefaultIndexerCarrier()

class ExplicitIndexerK : ExplicitIndexerCarrier()

class EventK : DefaultEventCarrier()

fun main() {
    val first: IPublicDefaultSlot = K1()
    val second: IPublicDefaultSlot = K2()
    first.M()
    second.M()
    K1().M()
    K2().M()
    val constructed: IPublicDefaultSlot = ConstructedK()
    constructed.M()
    val generic: IPublicGenericDefaultSlot<String> = GenericK()
    val external: IExternalDefaultSlot = ExternalK()
    val explicit: IExternalDefaultSlot = ExplicitK()
    val property: IPublicDefaultProperty = PropertyK()
    val nullable: String? = (NullabilityK() as IPublicNullabilityDefaultSlot).Normalize("ok")
    val indexer: IPublicDefaultIndexerSlot = IndexerK()
    val explicitIndexer: IPublicDefaultIndexerSlot = ExplicitIndexerK()
    EventK()
    println(generic.Echo("ok").length + external.Value() + explicit.Value() + property.Number +
        (nullable?.length ?: 0) - 2 + indexer[3] + explicitIndexer[3] +
        indexer["ab"] + explicitIndexer["ab"])
}
