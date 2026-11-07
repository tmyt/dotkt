using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

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
        static MultipleConstructors() { }
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
        MalformedEnumeration(paths);
        Console.Error.WriteLine("[declared metadata readers] self-test OK (generic contexts, inheritance, accessors, constructors)");
    }

    static void MalformedEnumeration(IEnumerable<string> paths)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("DeclaredReaderFixture.dll"),
            metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("DeclaredReaderFixture"), new Version(1, 0),
            default, default, default, AssemblyHashAlgorithm.None);
        var coreName = typeof(object).Assembly.GetName();
        var core = metadata.AddAssemblyReference(metadata.GetOrAddString(coreName.Name), coreName.Version,
            default, metadata.GetOrAddBlob(coreName.GetPublicKeyToken()), default, default);
        var objectType = metadata.AddTypeReference(core, metadata.GetOrAddString("System"),
            metadata.GetOrAddString("Object"));
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"),
            default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Abstract, default,
            metadata.GetOrAddString("Broken"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var signature = metadata.GetOrAddBlob(new byte[] { 0x20, 0, 1 }); // instance void ()
        const MethodAttributes methodFlags = MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual;
        metadata.AddMethodDefinition(methodFlags, default, metadata.GetOrAddString("Valid"), signature,
            0, MetadataTokens.ParameterHandle(1));
        metadata.AddMethodDefinition(methodFlags | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            default, metadata.GetOrAddString("CorruptName"), signature, 0, MetadataTokens.ParameterHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("Ambiguous"),
            objectType, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(3));
        for (var index = 0; index < 2; index++)
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                default, metadata.GetOrAddString(".ctor"), signature, 0, MetadataTokens.ParameterHandle(1));
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(), new MetadataRootBuilder(metadata),
            new BlobBuilder(), flags: CorFlags.ILOnly).Serialize(image);
        var path = Path.Combine(Path.GetTempPath(), $"dotkt-declared-readers-{Guid.NewGuid():N}.dll");
        try
        {
            var bytes = image.ToArray();
            using (var pe = new PEReader(new MemoryStream(bytes)))
            {
                var reader = pe.GetMetadataReader();
                var nameOffset = pe.PEHeaders.MetadataStartOffset
                    + reader.GetTableMetadataOffset(TableIndex.MethodDef)
                    + reader.GetTableRowSize(TableIndex.MethodDef) + 8;
                // This small fixture uses two-byte string indexes. Poison only the late
                // method's name after serialization, beyond the complete #Strings heap.
                if (reader.GetHeapSize(HeapIndex.String) >= ushort.MaxValue)
                    throw new Exception("fixture unexpectedly needs wide string indexes");
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(nameOffset), ushort.MaxValue);
            }
            File.WriteAllBytes(path, bytes);
            using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths.Append(path)), coreName.Name);
            var assembly = mlc.LoadFromAssemblyPath(path);
            var type = assembly.GetType("Broken", throwOnError: true);
            (Type Error, int Count) Observe(Func<IEnumerable<MethodInfo>> read)
            {
                var count = 0;
                try { foreach (var method in read()) count++; }
                catch (Exception exception) { return (exception.GetType(), count); }
                throw new Exception("malformed method name unexpectedly accepted");
            }
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var expected = Observe(() => type.GetMethods(flags));
            var actual = Observe(() => MetadataDeclaredMembers.Methods(type));
            if (expected.Count != 0 || actual != expected)
                throw new Exception($"late enumeration failure changed the index mutation boundary: expected {expected}, actual {actual}");
            var ambiguous = assembly.GetType("Ambiguous", throwOnError: true);
            try { ambiguous.GetConstructor(Type.EmptyTypes); throw new Exception("expected public binder ambiguity"); }
            catch (AmbiguousMatchException) { }
            try { MetadataDeclaredMembers.HasPublicParameterlessConstructor(ambiguous); throw new Exception("constructor ambiguity lost"); }
            catch (AmbiguousMatchException) { }
        }
        finally { File.Delete(path); }
    }
}
