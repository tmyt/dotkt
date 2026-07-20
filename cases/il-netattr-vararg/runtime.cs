using System;

namespace P
{
    // A .NET attribute whose ONLY constructor takes params object[] — must be applicable bare (zero args) from Kotlin.
    [AttributeUsage(AttributeTargets.All)]
    public class TagAttribute : Attribute
    {
        public TagAttribute(params object[] args) { Args = args; }
        public object[] Args;
    }
}
