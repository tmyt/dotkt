/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// Array binding annotations must be visible to common sources such as the unsigned array factories.
package kotlin.clr

// Marks an array FACTORY top-level function (`arrayOf`/`intArrayOf`/.../`arrayOfNulls`): a call to it CONSTRUCTS a native
// CLR array. bir2cir reads this marker off the REFERENCE assembly and emits `{k:newArray}` (from the vararg elements) for
// `kind` = "vararg", or `{k:newArraySized}` (from the `size` argument) for `kind` = "sized" (`arrayOfNulls`). The element
// type comes from `typeArgs[0]` (the generic `arrayOf<T>`/`arrayOfNulls<T>`) or the vararg element type (the concrete
// `intArrayOf`/... primitive factories). Replaces kotc's retired ARRAY_FACTORY_NAMES recognition.
@Target(AnnotationTarget.FUNCTION)
public annotation class ClrArrayFactory(val kind: String)
