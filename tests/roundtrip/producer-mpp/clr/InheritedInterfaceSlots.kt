package mpp.app

import inheritedclrnames.BoundChoice
import inheritedclrnames.PropertySlot
import inheritedclrnames.StringSlot

interface InheritedInterfaceSlots : PropertySlot, StringSlot, BoundChoice {
    fun <T : StringSlot> choose(value: T): Int
}
