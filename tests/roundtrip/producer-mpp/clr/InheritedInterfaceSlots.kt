package mpp.app

import inheritedclrnames.PropertySlot
import inheritedclrnames.StringSlot
import roundtrip.genericboundoverloads.BaseChoice
import roundtrip.genericboundoverloads.OtherChoice
import roundtrip.genericboundoverloads.RightBound

interface InheritedInterfaceSlots : PropertySlot, StringSlot

interface DerivedChoice : BaseChoice, OtherChoice { fun <T : RightBound> choose(value: T): Int }
