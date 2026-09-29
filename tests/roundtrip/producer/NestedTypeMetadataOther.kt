package nestedmetadata

// A second source root requires the same shared CharSequence representation.
fun <T : CharSequence> secondNestedText(value: T): T = value
