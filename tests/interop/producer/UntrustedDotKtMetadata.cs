// Negative provenance regression for PR #201 / issue #205. A third-party C# assembly is allowed to contain a
// full-name lookalike; without compiler provenance it must remain an ordinary C# assembly. The interop fixtures in
// this same DLL exercise BCL collection signatures that the old namespace-only detector incorrectly reverse-mapped.
using System;

namespace DotKt.Runtime.CompilerServices {
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class KotlinFileClassAttribute : Attribute { }
}
