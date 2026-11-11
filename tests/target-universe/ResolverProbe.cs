using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Toolchain;

static class ResolverProbe
{
    static readonly string Core = typeof(object).Assembly.Location;

    public static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("dotkt-resolver-");
        try
        {
            string Emit(string name, Action<MetadataBuilder> declarations, bool module = false)
            {
                var metadata = new MetadataBuilder();
                var fileName = name + (module ? ".netmodule" : ".dll");
                metadata.AddModule(0, metadata.GetOrAddString(fileName), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
                if (!module) metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 2, 3, 4),
                    default, default, default, AssemblyHashAlgorithm.None);
                metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"),
                    default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
                declarations(metadata);
                var image = new BlobBuilder();
                new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
                    new MetadataRootBuilder(metadata), new BlobBuilder()).Serialize(image);
                var path = Path.Combine(directory.FullName, fileName);
                File.WriteAllBytes(path, image.ToArray());
                return path;
            }
            TypeDefinitionHandle Type(MetadataBuilder metadata, string space, string name, TypeAttributes flags = TypeAttributes.Public) =>
                metadata.AddTypeDefinition(flags, metadata.GetOrAddString(space), metadata.GetOrAddString(name), default,
                    MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            var first = Emit("First", metadata =>
            {
                Type(metadata, "Physical", "Value");
                var outer = Type(metadata, "Physical", "Outer");
                var inner = Type(metadata, "", "Inner", TypeAttributes.NestedPrivate);
                metadata.AddNestedType(inner, outer);
                Type(metadata, "", "Global");
                Type(metadata, "Physical", "Odd+Name");
                Type(metadata, "Physical", "Hidden", TypeAttributes.NotPublic);
            });
            var second = Emit("Second", metadata => Type(metadata, "Physical", "Value"));
            string Forward(string name, string target) => Emit(name, metadata =>
            {
                var owner = metadata.AddAssemblyReference(metadata.GetOrAddString(target), new Version(1, 2, 3, 4), default, default, default, default);
                metadata.AddExportedType(TypeAttributes.Public | (TypeAttributes)0x00200000,
                    metadata.GetOrAddString("Physical"), metadata.GetOrAddString("Value"), owner, 0);
            });
            var forward = Forward("Forward", "First");
            var chain = Forward("Chain", "Forward");
            var missingForward = Forward("MissingForward", "Absent");
            var module = Emit("Extra", metadata => Type(metadata, "ModuleOnly", "Hidden", TypeAttributes.NotPublic), module: true);
            var multi = Emit("Multi", metadata => metadata.AddAssemblyFile(metadata.GetOrAddString(Path.GetFileName(module)), default, containsMetadata: true));

            void Check(string[] paths, string[] queries, string[]? runtime = null)
            {
                using var universe = new TargetReferenceUniverse(paths.Prepend(Core), runtime);
                foreach (var query in queries)
                {
                    var expected = Capture(() => FullScan(universe.Assemblies, runtime ?? [], query));
                    var actual = Capture(() => universe.ResolveType(query));
                    Equal(expected, actual, query);
                    Equal(actual, Capture(() => universe.ResolveType(query)), "cached " + query);
                    if (actual.type != null && !universe.Owns(actual.type)) throw new Exception("Non-target type returned");
                }
            }
            Check([first, forward, chain, multi], ["Physical.Value", "Physical.Outer+Inner", "Physical.Outer.Inner",
                "Physical.Hidden", "Global", "Physical.Odd\\+Name", "ModuleOnly.Hidden", "Missing.Type",
                "System.String[]", "System.Int32*", "System.Int32&", "system.String",
                "System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]]", "Physical.Outer+Missing"]);
            Check([multi, first], ["Physical.Value", "ModuleOnly.Hidden"]);
            Check([chain, forward, first], ["Physical.Value"]);
            Check([first, second], ["Physical.Value"]);
            Check([second, first], ["Physical.Value"], [second]);
            Check([first, second], ["Physical.Value"], [first, second]);
            Check([missingForward], ["Physical.Value"]);
            using (var owned = new TargetReferenceUniverse([Core, first, second]))
            {
                if (owned.ResolveType("Physical.Value", "second").Assembly.GetName().Name != "Second")
                    throw new Exception("Exact physical owner was lost");
                if (Capture(() => owned.ResolveType("Physical.Value", "Absent")).error != typeof(InvalidOperationException))
                    throw new Exception("Missing physical owner was accepted");
            }

            // Compare the authoritative framework path API with the single-open metadata identity API.
            CheckIdentities(Directory.GetFiles(Path.GetDirectoryName(Core)!, "*.dll").Concat([first, second, forward]));
            var valid = File.ReadAllBytes(first);
            using var pe = new PEReader(new MemoryStream(valid));
            string Mutate(string name, Action<byte[]> change)
            {
                var bytes = valid.ToArray();
                change(bytes);
                var path = Path.Combine(directory.FullName, name + ".dll");
                File.WriteAllBytes(path, bytes);
                return path;
            }
            var directoryOffset = pe.PEHeaders.PEHeaderStartOffset
                + (pe.PEHeaders.PEHeader!.Magic == PEMagic.PE32Plus ? 112 : 96) + 14 * 8;
            var native = Mutate("Native", bytes => Array.Clear(bytes, directoryOffset, 8));
            var badHeader = Mutate("BadHeader", bytes => Array.Clear(bytes, 0, 2));
            var badIdentity = Mutate("BadIdentity", bytes => Array.Clear(bytes, pe.PEHeaders.MetadataStartOffset, 4));
            if (ManagedReferenceCatalog.Create([native], "native probe").Entries.Count != 0)
                throw new Exception("Native image became managed");
            foreach (var runtime in new[] { false, true })
            {
                var failed = CaptureCatalog(badHeader, runtime);
                if (runtime ? failed != null : failed?.Contains("corrupt/truncated PE") != true)
                    throw new Exception("Header corruption classification changed");
                if (CaptureCatalog(badIdentity, runtime)?.Contains("unreadable CLI metadata / assembly identity") != true)
                    throw new Exception("Identity corruption must remain fatal, including runtime sets");
                if (CaptureCatalog(module, runtime)?.Contains("unreadable CLI metadata / assembly identity") != true)
                    throw new Exception("A netmodule cannot stand in for an assembly reference");
            }
            var reader = pe.GetMetadataReader();
            if (reader.GetHeapSize(HeapIndex.String) > ushort.MaxValue) throw new Exception("Unexpected fixture heap size");
            var namespaceOffset = pe.PEHeaders.MetadataStartOffset + reader.GetTableMetadataOffset(TableIndex.TypeDef)
                + reader.GetTableRowSize(TableIndex.TypeDef) + 4 + 2; // second TypeDef, after flags/name
            var badNamespace = Mutate("BadNamespace", bytes =>
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(namespaceOffset, 2), 0x7fff));
            string? namespaceError = null;
            try { using var invalid = new TargetReferenceUniverse([Core, badNamespace]); }
            catch (Exception error) { namespaceError = error.Message; }
            if (namespaceError?.Contains(badNamespace, StringComparison.Ordinal) != true)
                throw new Exception("Namespace-index error lost its reference path: " + namespaceError);
            var optionsPath = Path.Combine(directory.FullName, "options.json");
            const string optionsText = """
                {"outputFileName":"Example.dll","OutputTYPE":"Library","deterministic":true,
                 "noWin32Manifest":true,"win32Manifest":"manifest.xml","win32Resource":"input.res",
                 "applicationIcon":"icon.ico","nativeResource":"resolved.res","nativeResourceSdkAssembly":"sdk.dll",
                 "attributes":[{"type":"AssemblyVersion","value":"1.2.3.4","isLiteral":"false","typeName":"System.String"}]}
                """;
            File.WriteAllText(optionsPath, optionsText);
            var options = SdkAssemblyOptions.Read(optionsPath);
            var expectedOptions = JsonSerializer.Deserialize<SdkAssemblyOptions>(optionsText,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            if (!JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(expectedOptions)),
                JsonNode.Parse(JsonSerializer.Serialize(options, SdkAssemblyOptionsJsonContext.Default.SdkAssemblyOptions)))
                || options.Versions().Identity != new Version(1, 2, 3, 4))
                throw new Exception("Generated SDK option codec changed field/record/case semantics");
            Console.WriteLine("PASS resolver candidates: full-scan parity, forwarding/chains, duplicate definers/runtime selection, nested/private/escaped/module types, exact owners and identity/corruption classifications");
        }
        finally { directory.Delete(recursive: true); }
    }

    public static void CheckIdentities(IEnumerable<string> paths)
    {
        var count = 0;
        foreach (var path in paths.Distinct())
        {
            var catalog = ManagedReferenceCatalog.Create([path], "identity probe");
            if (catalog.Entries.Count == 0) continue; // Windows runtime folders also contain native DLLs.
            var expected = AssemblyName.GetAssemblyName(path);
            if (catalog.Entries.Single().Identity.FullName != expected.FullName
                || !catalog.Entries.Single().Identity.GetPublicKey()!.SequenceEqual(expected.GetPublicKey()!))
                throw new Exception("Assembly identity changed: " + path);
            count++;
        }
        Console.WriteLine($"PASS assembly identities: {count} managed images match the framework path API");
    }

    public static void CheckMerge(string emitterPath)
    {
        var emitter = Assembly.LoadFrom(Path.GetFullPath(emitterPath));
        var merge = emitter.GetType("IlEmit", throwOnError: true)!
            .GetMethod("MergeByFileClass", BindingFlags.Static | BindingFlags.NonPublic)!;
        // Use the actual production declaration type/codec from ilemit, not a second linked model assembly.
        var codec = emitter.GetType("DotKt.Cir.DeclarationJson", throwOnError: true)!;
        var declaration = emitter.GetType("DotKt.Cir.CirFileDeclaration", throwOnError: true)!;
        object Read(string json) => codec.GetMethod("Read")!.Invoke(null, [System.Text.Encoding.UTF8.GetBytes(json)])!;
        JsonNode Inspect(object value) => JsonNode.Parse((string)codec.GetMethod("Write")!.Invoke(null, [value, false])!)!;
        object Root(string file, string name, bool main = false) => Read($$$"""
            {"fileClass":"{{{file}}}","hasMain":{{{(main ? "true" : "false")}}},
             "methods":[{"name":"{{{name}}}","static":true,"override":false,"virtual":false,"params":[],"ret":{"t":"fqn","name":"void"},"body":[]}],
             "fields":[{"name":"{{{name}}}","type":{"t":"fqn","name":"System.Int32"}}],
             "properties":[{"name":"{{{name}}}","type":{"t":"fqn","name":"System.Int32"}}],
             "types":[{"name":"{{{name}}}","kind":"class"}],
             "assemblyAttrs":[{"attr":{"t":"fqn","name":"A"},"argTypes":[],"args":[]}]}
            """);
        var first = Root("Shared", "first");
        var other = Root("Other", "other");
        var synthetic = Root("", "Synthetic");
        var empty = Root("", "Empty");
        var continuation = Root("Shared", "second", true);
        System.Collections.IList Merge(params object[] documents)
        {
            var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(declaration))!;
            foreach (var document in documents) list.Add(document);
            return (System.Collections.IList)merge.Invoke(null, [list])!;
        }
        var distinct = Merge(first, synthetic, empty, other);
        var original = new[] { first, synthetic, empty, other };
        for (var index = 0; index < original.Length; index++)
            if (!ReferenceEquals(distinct[index], original[index]))
                throw new Exception("Distinct CIR containers were copied/reordered");
        var combined = Merge(first, synthetic, continuation, other, empty);
        var grouped = combined.Cast<object>().Select(Inspect).ToArray();
        if (combined.Count != 4 || !grouped[0]["hasMain"]!.GetValue<bool>()
            || grouped[1]["types"]![0]!["name"]!.GetValue<string>() != "Synthetic"
            || grouped[2]["fileClass"]!.GetValue<string>() != "Other"
            || grouped[3]["types"]![0]!["name"]!.GetValue<string>() != "Empty")
            throw new Exception("CIR grouping/order/synthetic independence changed");
        foreach (var key in new[] { "methods", "fields", "properties", "types", "assemblyAttrs" })
            if (grouped[0][key]!.AsArray().Count != 2)
                throw new Exception("CIR container merge dropped declarations: " + key);
        if (grouped[0]["methods"]![0]!["name"]!.GetValue<string>() != "first"
            || grouped[0]["methods"]![1]!["name"]!.GetValue<string>() != "second")
            throw new Exception("Merged CIR declaration order changed");
        Console.WriteLine("PASS CIR merge: distinct documents retained; shared file classes merge every declaration list in stable order");
    }

    static string? CaptureCatalog(string path, bool runtime)
    {
        try { ManagedReferenceCatalog.Create([path], "corruption probe", runtimeSelection: runtime); return null; }
        catch (ArgumentException error)
        {
            if (!error.Message.Contains(path, StringComparison.Ordinal))
                throw new Exception("Catalog error lost its reference path", error);
            return error.Message;
        }
    }

    static (Type? type, Type? error) Capture(Func<Type> action)
    {
        try { return (action(), null); }
        catch (Exception error) { return (null, error.GetType()); }
    }

    static void Equal((Type? type, Type? error) expected, (Type? type, Type? error) actual, string query)
    {
        if (expected != actual) throw new Exception($"Full-scan parity failed for {query}: {expected} != {actual}");
    }

    static Type FullScan(IReadOnlyList<Assembly> assemblies, string[] runtime, string name)
    {
        var matches = new List<Type>();
        foreach (var assembly in assemblies)
        {
            Type? type;
            try { type = assembly.GetType(name, throwOnError: false, ignoreCase: false); }
            catch (Exception error) { throw new InvalidOperationException("Full-scan lookup failed", error); }
            if (type != null && matches.All(candidate => candidate.Assembly != type.Assembly)) matches.Add(type);
        }
        if (matches.Count == 0)
        {
            var dot = name.LastIndexOf('.');
            if (dot > 0)
                try { return FullScan(assemblies, runtime, name[..dot] + "+" + name[(dot + 1)..]); }
                catch (NotSupportedException) { }
            throw new NotSupportedException();
        }
        if (matches.Count == 1) return matches[0];
        var identities = runtime.Select(path => AssemblyName.GetAssemblyName(path).FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = matches.Where(type => identities.Contains(type.Assembly.GetName().FullName)).ToArray();
        if (selected.Length == 1) return selected[0];
        throw new InvalidOperationException();
    }
}
