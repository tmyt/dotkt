package roundtrip.aliases

public typealias Cancellation = kotlin.coroutines.cancellation.CancellationException
public typealias Text = String
public typealias NullableText = Text?
public typealias Values<@AliasParameterMark T> = List<T>
public typealias AnyValues = List<*>
public typealias Callback<T> = T.(String) -> T
public typealias Suspended<T> = suspend T.(String) -> T
public typealias TextChain = Text

@Target(AnnotationTarget.TYPEALIAS, AnnotationTarget.TYPE_PARAMETER, AnnotationTarget.TYPE)
@Retention(AnnotationRetention.BINARY)
annotation class AliasTextMark(val value: String, val texts: Array<String> = [])

@AliasTextMark("prefix\ud800middle\udc00suffix😀\u0000", ["", "\ud800", "\udc00", "😀", "κ", "\u0000", "ANg="])
public typealias Utf16Text = @AliasTextMark("\ud800") String
public typealias Utf16Values<@AliasTextMark("\udc00") T> = List<T>

@Deprecated("Use Text instead")
public typealias DeprecatedText = String

internal typealias HiddenText = String
private typealias PrivateText = String
