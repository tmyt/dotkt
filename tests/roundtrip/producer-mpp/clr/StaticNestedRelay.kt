package mpp.app

import kotlin.clr.ClrField
import roundtrip.staticnested.StaticNestedOwner

@ClrField
val staticNestedField: StaticNestedOwner.Closed = StaticNestedOwner.Closed()
