package roundtrip.inheritedproperties

open class ImportedPropertySubclass : InheritedPropertyInterop.GenericPropertyMix<String, Int>("public", "slot")
fun readInheritedPublicProperty(value: ImportedPropertySubclass): String = value.Value
