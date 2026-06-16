// ilemit (D1.0 spike): emit a runnable .NET assembly *directly as IL* — no C# source, no csc.
// Proves the CIL toolchain: PersistedAssemblyBuilder + ManagedPEBuilder -> hello.dll + runtimeconfig.
//
//   ilemit <output-dir>
//
// Emits <output-dir>/hello.dll (+ hello.runtimeconfig.json) printing "Hello from IL".
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

static class IlEmit
{
    static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);

        var ab = new PersistedAssemblyBuilder(new AssemblyName("hello"), typeof(object).Assembly);
        ModuleBuilder mod = ab.DefineDynamicModule("hello");
        TypeBuilder program = mod.DefineType("Program", TypeAttributes.Public | TypeAttributes.Class);
        MethodBuilder main = program.DefineMethod(
            "Main", MethodAttributes.Public | MethodAttributes.Static, typeof(void), new[] { typeof(string[]) });

        ILGenerator il = main.GetILGenerator();
        il.Emit(OpCodes.Ldstr, "Hello from IL");
        il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", new[] { typeof(string) }));
        il.Emit(OpCodes.Ret);
        program.CreateType();

        // Serialize to a PE with an entry point (executable image).
        MetadataBuilder metadata = ab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData);
        MethodDefinitionHandle entryPoint = MetadataTokens.MethodDefinitionHandle(main.MetadataToken);
        var peHeader = new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll);
        var peBuilder = new ManagedPEBuilder(
            peHeader,
            new MetadataRootBuilder(metadata),
            ilStream,
            mappedFieldData: fieldData,
            entryPoint: entryPoint);

        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);

        var dllPath = Path.Combine(outDir, "hello.dll");
        using (var fs = new FileStream(dllPath, FileMode.Create, FileAccess.Write))
            peBlob.WriteContentTo(fs);

        // A runtimeconfig.json so `dotnet hello.dll` resolves the shared framework.
        var tfm = "net10.0";
        var version = Environment.Version; // e.g. 10.0.1
        File.WriteAllText(Path.Combine(outDir, "hello.runtimeconfig.json"),
            "{\n  \"runtimeOptions\": {\n    \"tfm\": \"" + tfm + "\",\n" +
            "    \"framework\": { \"name\": \"Microsoft.NETCore.App\", \"version\": \"" +
            version.Major + "." + version.Minor + ".0\" }\n  }\n}\n");

        Console.WriteLine($"emitted {dllPath}");
        return 0;
    }
}
