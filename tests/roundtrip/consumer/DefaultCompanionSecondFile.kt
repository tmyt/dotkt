import roundtrip.defaultcompanion.DefaultCompanionOwner
import roundtrip.defaultcompanion.Segment

fun capturedDefaultFromSecondFile(): String =
    DefaultCompanionOwner(Segment<Int>(null)).inlineCaptured({}, 61, "second file")
