package roundtrip.actualalias

public expect open class Cancellation(message: String?) : IllegalStateException

public typealias CommonCancellation = Cancellation
