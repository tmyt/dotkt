package explicitslotprobe

import Probe.ExplicitCollisionCarrier
import Probe.ExplicitEventCollisionCarrier
import Probe.ExplicitIndexerCollisionCarrier
import Probe.ExplicitPropertyCollisionCarrier

fun probe(): Int = ExplicitCollisionCarrier().Pick()

fun propertyProbe(): Int = ExplicitPropertyCollisionCarrier().Number

fun indexerProbe(): Int = ExplicitIndexerCollisionCarrier()[0]

fun eventProbe() = ExplicitEventCollisionCarrier().Updated
