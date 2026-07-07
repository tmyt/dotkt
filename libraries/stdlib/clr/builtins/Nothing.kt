/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

/**
 * Nothing has no instances. You can use Nothing to represent "a value that never exists": for example,
 * if a function has the return type of Nothing, it means that it never returns (always throws an exception).
 */
// @ClrTypeAlias to System.Object: the Kotlin bottom type has no CLR value; in any type slot it erases to `object`
// (a Nothing-returning method is a method that never returns normally). bir2cir reads this alias from the ref.dll
// and lowers kotlin.Nothing -> System.Object, exactly like kotlin.Any — mirroring the FoundationalRefAliases entry
// that the member-call substituter already uses. Without it, deleting the hardcoded KotlinToClr map (#55) would
// leave kotlin.Nothing un-lowered (identity), since Nothing carries no other alias source.
@kotlin.clr.ClrTypeAlias("System.Object")
public actual class Nothing private constructor()
