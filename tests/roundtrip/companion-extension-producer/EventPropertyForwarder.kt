package roundtrip.eventpropertyslot

import EventPropertySlotInterop.IMixedEvent

class ForwardedNative(source: IMixedEvent) : IMixedEvent by source
