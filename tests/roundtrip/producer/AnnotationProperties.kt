package roundtrip.annotationproperties

annotation class ImportedLabel(val value: String = "default", val code: Int = 11)
@ImportedLabel(code = 17) class ProducerDefaultLabel
annotation class ImportedArrays(val names: Array<String>, val counts: IntArray)
annotation class ImportedTags(vararg val tags: String)
annotation class ImportedTypes(val nested: ImportedLabel, val kind: DeprecationLevel)

private class ScopeTags { @DslMarker annotation class LayoutScope(val label: ImportedLabel) }
@DslMarker @Retention(AnnotationRetention.SOURCE) annotation class SourceOnlyLayout
@SourceOnlyLayout
@ScopeTags.LayoutScope(ImportedLabel("outer")) object OuterLayout { fun String.decorate() = this + "!" }
@ScopeTags.LayoutScope(ImportedLabel("inner")) object InnerLayout
fun outerLayout(body: OuterLayout.() -> Unit) = OuterLayout.body()
fun innerLayout(body: InnerLayout.() -> Unit) = InnerLayout.body()

@Target(AnnotationTarget.TYPE, AnnotationTarget.TYPE_PARAMETER, AnnotationTarget.FUNCTION)
annotation class CallbackMarker

@RequiresOptIn(message = "Preview API", level = RequiresOptIn.Level.ERROR)
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION)
annotation class PreviewMarker

@RequiresOptIn(level = RequiresOptIn.Level.WARNING)
annotation class PreviewWarning

@SubclassOptInRequired(PreviewMarker::class)
interface PreviewApi { fun name(): String }

fun producedLabel(): ImportedLabel = ImportedLabel("producer", 17)
fun producedArrays(): ImportedArrays = ImportedArrays(arrayOf("one", "two"), intArrayOf(7, 9))
fun producedTags(): ImportedTags = ImportedTags("one", "two")
fun producedTypes(): ImportedTypes = ImportedTypes(ImportedLabel("nested"), DeprecationLevel.WARNING)
