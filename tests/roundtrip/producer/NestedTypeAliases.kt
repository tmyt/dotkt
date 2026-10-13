package roundtrip.aliases

@Target(AnnotationTarget.TYPE_PARAMETER)
@Retention(AnnotationRetention.BINARY)
annotation class AliasParameterMark

class AliasOwner {
    typealias Nested = String
    typealias Values<T> = List<T>
}
