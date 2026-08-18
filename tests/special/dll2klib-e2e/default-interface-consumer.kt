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
import Probe.ExplicitEventCarrier
import Probe.ExternalExplicitEventCarrier
import Probe.PublicAndExplicitEventCarrier
import Probe.ExplicitShapeCarrier
import Probe.GenericDefaultCarrier
import Probe.OpenGenericDefaultCarrier
import Probe.IPublicDefaultSlot
import Probe.IPublicDefaultProperty
import Probe.IPublicDefaultIndexerSlot
import Probe.IPublicGenericDefaultSlot
import Probe.IPublicNullabilityDefaultSlot
import Probe.IPublicExplicitEventSlot
import Probe.IPublicExplicitShapeSlot
import Probe.NullabilityDefaultCarrier
import Probe.Contracts.IExternalDefaultSlot
import Probe.Contracts.IExternalExplicitEventSlot

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

class ExplicitEventK : ExplicitEventCarrier()

class ExternalExplicitEventK : ExternalExplicitEventCarrier()

class PublicAndExplicitEventK : PublicAndExplicitEventCarrier()

class ExplicitShapeK : ExplicitShapeCarrier()

class OpenGenericK : OpenGenericDefaultCarrier<String>()

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
    val explicitEvent = ExplicitEventK()
    val externalExplicitEvent = ExternalExplicitEventK()
    val publicAndExplicitEvent = PublicAndExplicitEventK()
    val explicitShape: ExplicitShapeCarrier = ExplicitShapeK()
    val openGeneric: IPublicGenericDefaultSlot<String> = OpenGenericK()
    var observed = 0
    explicitEvent.Changed.subscribe { observed += it }
    externalExplicitEvent.Changed.subscribe { observed += it }
    (externalExplicitEvent as IExternalExplicitEventSlot).Changed.subscribe { observed += it * 10 }
    publicAndExplicitEvent.Changed.subscribe { observed += it }
    (publicAndExplicitEvent as IPublicExplicitEventSlot).Changed.subscribe { observed += it * 10 }
    explicitEvent.Raise(4)
    externalExplicitEvent.Raise(5)
    publicAndExplicitEvent.RaisePublic(2)
    publicAndExplicitEvent.RaiseExplicit(3)
    val directNullable: String? = explicitShape.Normalize(null)
    val omittedNullable: String? = explicitShape.Normalize()
    val explicitText: String? = explicitShape.Text
    val explicitIndex: String? = (explicitShape as IPublicExplicitShapeSlot)[null]
    println(generic.Echo("ok").length + openGeneric.Echo("ok").length - 2 +
        external.Value() + explicit.Value() + property.Number +
        (nullable?.length ?: 0) - 2 + indexer[3] + explicitIndexer[3] +
        indexer["ab"] + explicitIndexer["ab"] + observed +
        (directNullable?.length ?: 0) + (omittedNullable?.length ?: 0) +
        (explicitText?.length ?: 0) + (explicitIndex?.length ?: 0))
}
