// DotKt metadata attributes — carry the Kotlin-language facts that have NO native .NET representation, so a
// DotKt-compiled assembly can be consumed as KOTLIN (not just as a plain .NET assembly). They live here in
// DotKt.Runtime for cross-assembly identity (every DotKt assembly already references it — see memory
// dotkt-naming-and-runtime-split): ilemit stamps them onto the emitted IL, and the compiler's FIR injector
// (via facadegen --meta) reads them back and restores the corresponding Kotlin modifier on the synthesized FIR.
//
// SCOPE: only modifiers that .NET metadata can't already express. `final/open/abstract` (modality) and visibility
// round-trip through plain .NET virtual-ness/accessibility, so they need no attribute. `inline` is intentionally
// absent: cross-assembly inlining needs the function BODY carried in metadata (what Kotlin's @Metadata does) — a
// separate, larger effort — and marking a function `inline` without a body would break the frontend.
using System;

namespace DotKt.Metadata
{
    /// <summary>Kotlin function modifiers with no .NET analog, stamped on the emitted method.</summary>
    [Flags]
    public enum KotlinFunctionFlags
    {
        None = 0,
        /// <summary>Kotlin `infix fun` — callable with infix notation (`a foo b`).</summary>
        Infix = 1,
        /// <summary>Kotlin `operator fun` — participates in operator/convention resolution (`+`, `[]`, `in`, `invoke`, ...).</summary>
        Operator = 2,
        /// <summary>Kotlin `suspend fun` — emitted with the CLR `Task&lt;T&gt;` ABI; restored as `suspend fun(...): T`.</summary>
        Suspend = 4,
        // Reserved for future round-tripping (each needs more than a flag): Tailrec, Inline (needs the body), External.
    }

    /// <summary>Marks a method with the Kotlin modifiers it was declared with (those .NET can't express).</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class KotlinFunctionAttribute : Attribute
    {
        public KotlinFunctionFlags Flags { get; }
        public KotlinFunctionAttribute(KotlinFunctionFlags flags) { Flags = flags; }
    }

    /// <summary>
    /// Carries the BIR body of an `inline fun` that takes a lambda, so a consuming Kotlin module can splice it at the
    /// call site (DotKt inlines at emit time, so the body must travel). This is the only way a cross-module non-local
    /// `return` through the lambda can work. <see cref="Body"/> is the compact `{params, body}` BIR JSON.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class KotlinInlineAttribute : Attribute
    {
        public string Body { get; }
        public KotlinInlineAttribute(string body) { Body = body; }
    }

    /// <summary>
    /// Carries the Kotlin NULLABILITY of a method's signature (.NET reference types don't distinguish `String` from
    /// `String?`). A bitmask: bit 0 = the return type is nullable; bit (i+1) = parameter i is nullable. Absent = all
    /// non-null. The injector restores `T?` on the marked positions so a consumer can pass/handle null soundly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class KotlinNullableAttribute : Attribute
    {
        public uint Mask { get; }
        public KotlinNullableAttribute(uint mask) { Mask = mask; }
    }

    /// <summary>
    /// Declares that a Kotlin package prefix projects to a different .NET namespace prefix in THIS assembly — so a
    /// DotKt-built library can live in (say) <c>DotKt.Coroutines</c> yet be consumed with idiomatic Kotlin
    /// <c>import kotlinx.coroutines.*</c>. A consuming module reads these off its referenced assemblies and rewrites
    /// both directions: an import's package -> the real .NET namespace (to resolve types), and a resolved type's .NET
    /// namespace -> the Kotlin package (so it's exposed under the package the user imported). Prefix-based, so
    /// sub-packages (<c>kotlinx.coroutines.flow</c>) follow automatically. Multiple mappings are allowed.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
    public sealed class DotKtNamespaceProjectionAttribute : Attribute
    {
        /// <summary>The Kotlin package prefix the consumer writes (e.g. <c>kotlinx.coroutines</c>).</summary>
        public string KotlinPrefix { get; }
        /// <summary>The real .NET namespace prefix the types live in (e.g. <c>DotKt.Coroutines</c>).</summary>
        public string DotNetPrefix { get; }
        public DotKtNamespaceProjectionAttribute(string kotlinPrefix, string dotNetPrefix) { KotlinPrefix = kotlinPrefix; DotNetPrefix = dotNetPrefix; }
    }

    /// <summary>
    /// Marks a public backing FIELD whose Kotlin property is not publicly settable (`val`, or `var ... private set`).
    /// The field stays public (same-module/CLR writers use it), but a consuming Kotlin module restores the property as
    /// read-only (`val`) so an external `x.n = ...` is rejected — matching Kotlin's view of the original declaration.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class KotlinReadOnlyAttribute : Attribute
    {
        public KotlinReadOnlyAttribute() { }
    }

    /// <summary>
    /// Marks a synthetic file-facade class (Kotlin top-level declarations compile to static members of a
    /// <c>&lt;File&gt;Kt</c> class). The injector exposes its static methods as TOP-LEVEL Kotlin functions in the
    /// package = the class's .NET namespace, instead of as members of a class.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class KotlinFileClassAttribute : Attribute
    {
        public KotlinFileClassAttribute() { }
    }
}
