package roundtrip.reifiednullability

public inline fun <reified T> matches(value: Any?): Boolean = value is T

public inline fun <reified T> forwarded(value: Any?): Boolean = matches<T>(value)

public inline fun <reified T> delayed(value: Any?): Boolean = ({ value is T })()

public inline fun <reified T> objectDelayed(value: Any?): Boolean = object {
    public fun matches(): Boolean = value is T
}.matches()

public fun interface ReifiedChecker { public fun matches(value: Any?): Boolean }

public inline fun <reified T> checker(): ReifiedChecker = ReifiedChecker { it is T }

public inline fun <reified T> suspended(value: Any?): suspend () -> Boolean = { value is T }

public inline fun <reified A, reified B> secondDelayed(value: Any?): Boolean = ({ value is B })()

public inline fun <reified A, reified B> secondObjectDelayed(value: Any?): Boolean = object {
    public fun matches(): Boolean = value is B
}.matches()

public inline fun <reified A, reified B> secondChecker(): ReifiedChecker = ReifiedChecker { it is B }

public inline fun <reified A, reified B> secondSuspended(value: Any?): suspend () -> Boolean = { value is B }

public class ReifiedPropertyCarrier<T>(public var value: Any?)

public class ReifiedPropertyContext(public val value: Any?)

public class SecondReifiedPropertyCarrier<A, B>(public val value: Any?)

public inline val <reified T> ReifiedPropertyCarrier<T>.propertyMatches: Boolean
    get() = value is T

public inline var <reified T> ReifiedPropertyCarrier<T>.reifiedPropertyValue: T?
    get() = value as? T
    set(value) { this.value = value }

context(context: ReifiedPropertyContext)
public inline val <reified T> ReifiedPropertyCarrier<T>.contextPropertyMatches: Boolean
    get() = value is T && context.value is T

public inline val <A, reified B> SecondReifiedPropertyCarrier<A, B>.secondPropertyMatches: Boolean
    get() = value is B

@Suppress("UNCHECKED_CAST")
public val <T> ReifiedPropertyCarrier<T>.ordinaryPropertyValue: T?
    get() = value as T?
