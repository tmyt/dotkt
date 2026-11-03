namespace Utf16LiteralInterop;

public static class Api
{
    public const char High = '\ud800';
    public const char Low = '\udfff';
    public const string Text = "\ud800X\udfff";
    public static string ConstantText() => Text;
    public static char DefaultChar(char value = '\udbff') => value;
    public static string DefaultText(string value = "\udc00") => value;
}
