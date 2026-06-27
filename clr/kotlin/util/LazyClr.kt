@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
// Step-1 CLR stub mirroring JVM actual; bodies are TODO pending @Clr/BCL binding.
// See docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP".

package kotlin

public actual fun <T> lazy(initializer: () -> T): Lazy<T> = TODO("clr binding should be implemented")

public actual fun <T> lazy(mode: LazyThreadSafetyMode, initializer: () -> T): Lazy<T> = TODO("clr binding should be implemented")

public actual fun <T> lazy(lock: Any?, initializer: () -> T): Lazy<T> = TODO("clr binding should be implemented")
