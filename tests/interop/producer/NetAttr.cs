// Producer source for the migrated il-netattr case (#54). An existing .NET attribute, applied from Kotlin as an
// annotation. Own namespace (NetAttr) — the case's original `namespace P` collided with the other P-namespace
// cases (outref/selfref/netattr-vararg) once they share this single producer assembly.
using System;
namespace NetAttr {
    [AttributeUsage(AttributeTargets.All)]
    public class LabelAttribute : Attribute {
        public LabelAttribute(string text, int priority) { Text = text; Priority = priority; }
        public string Text;
        public int Priority;
    }
}
