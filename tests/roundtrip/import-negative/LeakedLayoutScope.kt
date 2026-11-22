import roundtrip.annotationproperties.*

fun useImportedScopes() = outerLayout {
    innerLayout { "text".decorate() }
}

fun cannotNameMarkerOwner() = ScopeTags::class
