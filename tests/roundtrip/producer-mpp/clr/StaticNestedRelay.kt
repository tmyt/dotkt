package mpp.app

import kotlin.clr.ClrField
import roundtrip.staticnested.StaticNestedOwner

@ClrField
val staticNestedField: StaticNestedOwner.Closed = StaticNestedOwner.Closed()

fun relayStaticTokens(values: List<StaticNestedOwner.Token<String>>): List<StaticNestedOwner.Token<String>> = values
