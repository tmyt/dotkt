/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

/**
 * The base class for all errors and exceptions. Only instances of this class can be thrown or caught.
 *
 * @param message the detail message string.
 * @param cause the cause of this throwable.
 */
public actual open class Throwable actual constructor(public open actual val message: String?, public open actual val cause: Throwable?) {
    public actual constructor(message: String?) : this(message, null)

    public actual constructor(cause: Throwable?) : this(cause?.toString(), cause)

    public actual constructor() : this(null, null)
}

public actual open class Error : Throwable {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
    public actual constructor(message: String?, cause: Throwable?) : super(message, cause)
    public actual constructor(cause: Throwable?) : super(cause)
}

public actual open class Exception : Throwable {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
    public actual constructor(message: String?, cause: Throwable?) : super(message, cause)
    public actual constructor(cause: Throwable?) : super(cause)
}

public actual open class RuntimeException : Exception {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
    public actual constructor(message: String?, cause: Throwable?) : super(message, cause)
    public actual constructor(cause: Throwable?) : super(cause)
}

public actual open class IllegalArgumentException : RuntimeException {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
    public actual constructor(message: String?, cause: Throwable?) : super(message, cause)
    public actual constructor(cause: Throwable?) : super(cause)
}

public actual open class IllegalStateException : RuntimeException {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
    public actual constructor(message: String?, cause: Throwable?) : super(message, cause)
    public actual constructor(cause: Throwable?) : super(cause)
}

public actual open class IndexOutOfBoundsException : RuntimeException {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
}

public actual open class ConcurrentModificationException : RuntimeException {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
    public actual constructor(message: String?, cause: Throwable?) : super(message, cause)
    public actual constructor(cause: Throwable?) : super(cause)
}

public actual open class UnsupportedOperationException : RuntimeException {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
    public actual constructor(message: String?, cause: Throwable?) : super(message, cause)
    public actual constructor(cause: Throwable?) : super(cause)
}

public actual open class NumberFormatException : IllegalArgumentException {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
}

public actual open class NullPointerException : RuntimeException {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
}

public actual open class ClassCastException : RuntimeException {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
}

public actual open class AssertionError : Error {
    public actual constructor() : super()
    public actual constructor(message: Any?) : super(message?.toString())

    @SinceKotlin("1.9")
    public actual constructor(message: String?, cause: Throwable?) : super(message, cause)
}

public actual open class NoSuchElementException : RuntimeException {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
}

@SinceKotlin("1.3")
public actual open class ArithmeticException : RuntimeException {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
}

@Deprecated("This exception type is not supposed to be thrown or caught in common code and will be removed from kotlin-stdlib-common soon.", level = DeprecationLevel.ERROR)
public actual open class NoWhenBranchMatchedException : RuntimeException {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
    public actual constructor(message: String?, cause: Throwable?) : super(message, cause)
    public actual constructor(cause: Throwable?) : super(cause)
}

@Deprecated("This exception type is not supposed to be thrown or caught in common code and will be removed from kotlin-stdlib-common soon.", level = DeprecationLevel.ERROR)
public actual class UninitializedPropertyAccessException : RuntimeException {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
    public actual constructor(message: String?, cause: Throwable?) : super(message, cause)
    public actual constructor(cause: Throwable?) : super(cause)
}
