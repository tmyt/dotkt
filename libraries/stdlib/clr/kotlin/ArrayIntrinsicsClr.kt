@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

/**
 * Returns an empty array of the specified type [T].
 */
// THE ALLOCATION OF A GENUINE `T[]` FOR A REIFIED ELEMENT. Two shapes that look like they would serve do not:
//   * `arrayOfNulls<T>(n)` honestly returns `Array<T?>`, which is `object[]` (#86 D2) — not castable to `int32[]`; and
//   * the Kotlin array constructor `Array<T>(n) { … }` is refused for a bare type parameter by kotc (its `Func<int,T>`
//     init would be a TypeBuilderInstantiation), so it silently produces an empty array here.
// `T::class` IS the `System.Type` on the CLR, so `Array.CreateInstance(elementType, length)` builds the exact `T[]`,
// zero-filled. Every reified `Array<T>` factory in the CLR stdlib allocates through this one helper.
@PublishedApi
@kotlin.clr.ClrIntrinsic("System.Array.CreateInstance")   // static Array.CreateInstance(Type, int) -> Array
internal fun dotktNewTypedArray(elementType: Any, length: Int): Any = TODO("clr binding should be implemented")

@Suppress("UNCHECKED_CAST")
public actual inline fun <reified T> emptyArray(): Array<T> = dotktNewTypedArray(T::class, 0) as Array<T>
