using System.Reflection;
using System.Runtime.ExceptionServices;

// Adapter for the pinned MetadataLoadContext dependency's declared-member readers.
// The public name-query layer allocates 24 caches per type even for full enumeration.
// These readers retain its reflection objects and decoding/validation, without name queries.
static class MetadataDeclaredMembers
{
    static readonly Type MetadataType = typeof(MetadataLoadContext).Assembly
        .GetType("System.Reflection.TypeLoading.RoType", throwOnError: true);
    static readonly Type NameFilter = typeof(MetadataLoadContext).Assembly
        .GetType("System.Reflection.NameFilter", throwOnError: true);
    static readonly MethodInfo MethodsReader = Reader<MethodInfo>("GetMethodsCore", true);
    static readonly MethodInfo PropertiesReader = Reader<PropertyInfo>("GetPropertiesCore", true);
    static readonly MethodInfo ConstructorsReader = Reader<ConstructorInfo>("GetConstructorsCore", false);

    static MethodInfo Reader<T>(string name, bool reflectedType) where T : MemberInfo
    {
        var reader = MetadataType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic,
            null, reflectedType ? new[] { NameFilter, typeof(Type) } : new[] { NameFilter }, null);
        if (reader?.ReturnType != typeof(IEnumerable<T>))
            throw new MissingMethodException(MetadataType.FullName, name);
        return reader;
    }

    internal static void EnsureAvailable()
    {
        // Force the internal dependency contract check outside per-reference/type catches.
        _ = MethodsReader;
        _ = PropertiesReader;
        _ = ConstructorsReader;
    }

    static IEnumerable<T> Read<T>(MethodInfo reader, Type type, bool reflectedType) where T : MemberInfo
    {
        if (!MetadataType.IsInstanceOfType(type))
            throw new ArgumentException("A MetadataLoadContext type is required", nameof(type));
        try
        {
            // Public GetMembers materializes before the caller mutates its index. Preserve
            // that failure boundary if a later metadata row cannot be enumerated.
            return ((IEnumerable<T>)reader.Invoke(type,
                reflectedType ? new object[] { null, type } : new object[] { null })).ToArray();
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
