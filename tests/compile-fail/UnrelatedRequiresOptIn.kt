@RequiresOptIn(message = "UnrelatedPreview requires its own opt-in")
@Retention(AnnotationRetention.BINARY)
@Target(AnnotationTarget.FUNCTION)
annotation class UnrelatedPreview

@UnrelatedPreview
fun unrelatedExperimentalApi() = Unit

fun main() = unrelatedExperimentalApi()
