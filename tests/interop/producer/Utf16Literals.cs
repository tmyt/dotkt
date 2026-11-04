using System;
using System.Linq;
using System.Reflection;

namespace Utf16LiteralInterop;

[AttributeUsage(AttributeTargets.Class)]
public sealed class StringPayloadAttribute(string value) : Attribute { public string Value { get; } = value; }

[StringPayload("\ud800")]
public sealed class CSharpStringAttributeControl { }

[AttributeUsage(AttributeTargets.Class)]
public sealed class CharPayloadAttribute(char scalar, char[] elements) : Attribute
{
    public char Scalar { get; } = scalar;
    public char[] Elements { get; } = elements;
    public char Named;
    public char[] NamedArray { get; set; } = [];
}

public static class Api
{
    public const char High = '\ud800';
    public const char Low = '\udfff';
    public const string Text = "\ud800X\udfff";
    public const string ScalarText = "A\ud83d\ude00\ufffd";
    public static string ConstantText() => Text;
    public static char DefaultChar(char value = '\udbff') => value;
    public static string DefaultText(string value = "\udc00") => value;
    public static string DefaultPair(string value = "\ud83d\ude00") => value;
    public static int StringAttributeUnit(object target) => target.GetType().GetCustomAttribute<StringPayloadAttribute>()!.Value[0];
    public static int CSharpStringAttributeUnit() => StringAttributeUnit(new CSharpStringAttributeControl());

    public static string AttributeUnits(object target)
    {
        var attr = target.GetType().GetCustomAttribute<CharPayloadAttribute>()!;
        return $"{(int)attr.Scalar}|{string.Join(',', attr.Elements.Select(c => (int)c))}|{(int)attr.Named}|{string.Join(',', attr.NamedArray.Select(c => (int)c))}";
    }
}
