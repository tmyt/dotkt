// Producer source for the migrated il-netattr-vararg case (#184). A .NET attribute whose ONLY constructor takes
// `params object[]` — must be applicable bare (zero args) from Kotlin (the vararg param surfaces as vararg in the
// projected annotation class, not as a required argument). Own namespace (NetAttrVararg) to avoid the `namespace P`
// collision with netattr/outref/selfref in this single producer assembly.
using System;
namespace NetAttrVararg {
    [AttributeUsage(AttributeTargets.All)]
    public class TagAttribute : Attribute {
        public TagAttribute(params object[] args) { Args = args; }
        public object[] Args;
    }
}
