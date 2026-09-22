import InheritedStatics.StringLeaf

// Keep defaults in a different source file from their callers: the owner fact
// belongs to this expression's source, even when emitted in a caller's body.
internal fun inheritedStaticPropertyDefault(value: String = StringLeaf.Property): String = value
internal fun inheritedStaticMethodDefault(value: String = StringLeaf.Store("method default")): String = value
