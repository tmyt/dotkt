using System.Reflection;
using System.Runtime.ExceptionServices;

// Adapter for the pinned MetadataLoadContext dependency's declared-member readers.
// The public name-query layer allocates 24 caches per type even for full enumeration.
// These readers retain its reflection objects and decoding/validation, without name queries.
static class MetadataDeclaredMembers
{
    static readonly Type MetadataType = typeof(MetadataLoadContext).Assembly
        .GetType("System.Reflection.TypeLoading.RoType", throwOnError: true);
    static readonly MethodInfo MethodsReader = Reader("GetMethodsCore");
    static readonly MethodInfo PropertiesReader = Reader("GetPropertiesCore");
    static readonly MethodInfo ConstructorsReader = Reader("GetConstructorsCore");

    static MethodInfo Reader(string name) => MetadataType.GetMethod(name,
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(MetadataType.FullName, name);

    static IEnumerable<T> Read<T>(MethodInfo reader, Type type, bool reflectedType) where T : MemberInfo
    {
        if (!MetadataType.IsInstanceOfType(type))
            throw new ArgumentException("A MetadataLoadContext type is required", nameof(type));
        try
        {
            return (IEnumerable<T>)reader.Invoke(type,
                reflectedType ? new object[] { null, type } : new object[] { null });
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    internal static IEnumerable<MethodInfo> Methods(Type type) => Read<MethodInfo>(MethodsReader, type, true);
    internal static IEnumerable<PropertyInfo> Properties(Type type) => Read<PropertyInfo>(PropertiesReader, type, true);
    internal static IEnumerable<ConstructorInfo> Constructors(Type type) => Read<ConstructorInfo>(ConstructorsReader, type, false);

    internal static bool HasPublicParameterlessConstructor(Type type)
    {
        var found = false;
        foreach (var constructor in Constructors(type))
        {
            if (!constructor.IsPublic || constructor.IsStatic || constructor.GetParameters().Length != 0) continue;
            if (found) return type.GetConstructor(Type.EmptyTypes) is ConstructorInfo { IsPublic: true };
            found = true;
        }
        return found;
    }
}
