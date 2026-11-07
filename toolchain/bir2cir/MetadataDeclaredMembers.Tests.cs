using System.Reflection;

static class MetadataDeclaredMembersTests
{
    interface IValue { int Value { get; } }
    class Base<T> { public virtual T Value { get; set; } }
    class Derived<T> : Base<T>, IValue
    {
        public override T Value { get; set; }
        int IValue.Value => 1;
        public T this[int index, string key] { get => default; set { } }
        public string WriteOnly { set { } }
        private static int Hidden { get; set; }
        public Derived() { }
        private Derived(T value) { }
    }
    class PrivateConstructor { private PrivateConstructor() { } }
    class ArgumentConstructor { public ArgumentConstructor(int value) { } }
    class MultipleConstructors
    {
        public MultipleConstructors() { }
        public MultipleConstructors(int value) { }
    }

    internal static void SelfTest()
    {
        var paths = Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location), "*.dll")
            .Append(typeof(MetadataDeclaredMembersTests).Assembly.Location);
        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths),
            typeof(object).Assembly.GetName().Name);
        var assembly = mlc.LoadFromAssemblyPath(typeof(MetadataDeclaredMembersTests).Assembly.Location);
        var derived = assembly.GetType(typeof(Derived<>).FullName, throwOnError: true);
        var types = new[] { derived,
            derived.MakeGenericType(mlc.CoreAssembly.GetType("System.String", throwOnError: true)),
            assembly.GetType(typeof(PrivateConstructor).FullName, throwOnError: true),
            assembly.GetType(typeof(ArgumentConstructor).FullName, throwOnError: true),
            assembly.GetType(typeof(MultipleConstructors).FullName, throwOnError: true) };
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        void EqualMembers(MemberInfo[] expected, MemberInfo[] actual)
        {
            if (expected.Length != actual.Length)
                throw new Exception("declared metadata member count changed");
            for (var index = 0; index < expected.Length; index++)
                if (expected[index].MetadataToken != actual[index].MetadataToken
                    || expected[index].DeclaringType != actual[index].DeclaringType
                    || expected[index].ReflectedType != actual[index].ReflectedType)
                    throw new Exception("declared metadata identity/context/order changed");
        }
        foreach (var type in types)
        {
            EqualMembers(type.GetMethods(flags), MetadataDeclaredMembers.Methods(type).ToArray());
            var expected = type.GetProperties(flags);
            var actual = MetadataDeclaredMembers.Properties(type).ToArray();
            EqualMembers(expected, actual);
            for (var index = 0; index < expected.Length; index++)
                if (expected[index].GetIndexParameters().Length != actual[index].GetIndexParameters().Length
                    || expected[index].GetGetMethod(true)?.MetadataToken != actual[index].GetGetMethod(true)?.MetadataToken
                    || expected[index].GetSetMethod(true)?.MetadataToken != actual[index].GetSetMethod(true)?.MetadataToken)
                    throw new Exception("declared property accessor/indexer facts changed");
            EqualMembers(type.GetConstructors(flags & ~BindingFlags.Static),
                MetadataDeclaredMembers.Constructors(type).Where(constructor => !constructor.IsStatic).ToArray());
            if (MetadataDeclaredMembers.HasPublicParameterlessConstructor(type)
                != (type.GetConstructor(Type.EmptyTypes) is ConstructorInfo { IsPublic: true }))
                throw new Exception("public parameterless-constructor fact changed");
        }
        Console.Error.WriteLine("[declared metadata readers] self-test OK (generic contexts, inheritance, accessors, constructors)");
    }
}
