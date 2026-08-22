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
