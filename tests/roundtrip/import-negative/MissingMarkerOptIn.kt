import roundtrip.annotationproperties.PreviewMarker

@PreviewMarker private fun preview() = Unit
private fun missingOptIn() = preview()
