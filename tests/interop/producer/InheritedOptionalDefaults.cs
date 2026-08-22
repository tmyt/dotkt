namespace InheritedOptionalDefaults;

public class BaseWriter
{
    public string Save(string path, int? quality = null) =>
        $"{path}:{(quality.HasValue ? quality.Value.ToString() : "default")}";
}

public sealed class DerivedWriter : BaseWriter
{
    public string Save(int id, int? quality = 99) =>
        $"{id}:{(quality.HasValue ? quality.Value.ToString() : "default")}";
}

public class GenericBaseWriter<T>
{
    public string Save(T value, int quality = 1) => $"{value}:{quality}";
}

public sealed class GenericDerivedWriter<T> : GenericBaseWriter<T>
{
    public string Save(string value, int quality = 99) => $"{value}:{quality}";
}

public class ValueBaseWriter
{
    public string Save(string value, int quality = 5) => $"{value}:{quality}";
}

public sealed class HidingDerivedWriter : ValueBaseWriter
{
    public new string Save(string value, int quality = 7) => $"{value}:{quality}";
}

public interface IBaseWriter
{
    string Save(string value, int quality = 1);
}

public interface IDerivedWriter : IBaseWriter
{
    string Save(int value, int quality = 99);
}

public sealed class InterfaceWriter : IDerivedWriter
{
    public string Save(string value, int quality = 1) => $"{value}:{quality}";
    public string Save(int value, int quality = 99) => $"{value}:{quality}";
}

public enum NavigationMethod
{
    Unspecified = 0,
    Directional = 2,
}

[System.Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
}

public sealed class EnumDefaults
{
    public string Focus(
        NavigationMethod method = NavigationMethod.Unspecified,
        KeyModifiers modifiers = KeyModifiers.None) => $"{(int)method}:{(int)modifiers}";

    public string Move(
        NavigationMethod method = NavigationMethod.Directional,
        KeyModifiers modifiers = KeyModifiers.Alt | KeyModifiers.Shift) => $"{(int)method}:{(int)modifiers}";
}

public static class StaticEnumDefaults
{
    public static string Move(
        NavigationMethod method = NavigationMethod.Directional,
        KeyModifiers modifiers = KeyModifiers.Alt | KeyModifiers.Shift) => $"{(int)method}:{(int)modifiers}";
}
