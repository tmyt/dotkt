using System;

namespace P
{
    // An existing .NET attribute, applied from Kotlin as an annotation (#54).
    [AttributeUsage(AttributeTargets.All)]
    public class LabelAttribute : Attribute
    {
        public LabelAttribute(string text, int priority) { Text = text; Priority = priority; }
        public string Text;
        public int Priority;
    }
}
