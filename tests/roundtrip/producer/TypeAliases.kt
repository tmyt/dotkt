package roundtrip.aliases

public typealias Cancellation = kotlin.coroutines.cancellation.CancellationException
public typealias Text = String
public typealias NullableText = Text?
public typealias Values<T> = List<T>
public typealias AnyValues = List<*>
public typealias Callback<T> = T.(String) -> T
public typealias Suspended<T> = suspend T.(String) -> T
public typealias TextChain = Text

@Deprecated("Use Text instead")
public typealias DeprecatedText = String

internal typealias HiddenText = String
private typealias PrivateText = String
