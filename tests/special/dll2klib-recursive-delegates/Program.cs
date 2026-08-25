using System.Reflection;
using System.Reflection.Emit;

if (args.Length != 1)
    throw new ArgumentException("usage: Generator <output-directory>");

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
WriteLocalCycle(output);
WriteGenericCycle(output);
WriteCrossAssemblyCycle(output);
WriteReturnCycle(output);
WriteArrayCycle(output);
WriteGenericContainerCycle(output);
WriteErasedModifierCycle(output);

static void WriteLocalCycle(string output)
{
    var assembly = NewAssembly("Recursive.Local", out var module);
    var a = NewDelegate(module, "Recursive.Local.A");
    var b = NewDelegate(module, "Recursive.Local.B");
    CompleteDelegate(a, typeof(void), b);
    CompleteDelegate(b, typeof(void), a);
    AddApi(module, "Recursive.Local.Api", ("UseA", a), ("UseB", b));
    a.CreateType();
    b.CreateType();
    assembly.Save(Path.Combine(output, "Recursive.Local.dll"));
}

static void WriteGenericCycle(string output)
{
    var assembly = NewAssembly("Recursive.Generic", out var module);
    var self = NewDelegate(module, "Recursive.Generic.Self`1");
    var typeParameter = self.DefineGenericParameters("T")[0];
    var constructedSelf = self.MakeGenericType(typeParameter);
    CompleteDelegate(self, typeof(void), constructedSelf);
    AddApi(module, "Recursive.Generic.Api", ("Use", self.MakeGenericType(typeof(int))));
    self.CreateType();
    assembly.Save(Path.Combine(output, "Recursive.Generic.dll"));
}

static void WriteCrossAssemblyCycle(string output)
{
    var assemblyA = NewAssembly("Recursive.CrossA", out var moduleA);
    var assemblyB = NewAssembly("Recursive.CrossB", out var moduleB);
    var a = NewDelegate(moduleA, "Recursive.Cross.A");
    var b = NewDelegate(moduleB, "Recursive.Cross.B");
    CompleteDelegate(a, typeof(void), b);
    CompleteDelegate(b, typeof(void), a);
    AddApi(moduleA, "Recursive.Cross.ApiA", ("Use", a));
    AddApi(moduleB, "Recursive.Cross.ApiB", ("Use", b));
    a.CreateType();
    b.CreateType();
    assemblyA.Save(Path.Combine(output, "Recursive.CrossA.dll"));
    assemblyB.Save(Path.Combine(output, "Recursive.CrossB.dll"));
}

static void WriteReturnCycle(string output)
{
    var assembly = NewAssembly("Recursive.Return", out var module);
    var self = NewDelegate(module, "Recursive.Return.Self");
    CompleteDelegate(self, self);
    AddApi(module, "Recursive.Return.Api", ("Use", self));
    self.CreateType();
    assembly.Save(Path.Combine(output, "Recursive.Return.dll"));
}

static void WriteArrayCycle(string output)
{
    var assembly = NewAssembly("Recursive.Array", out var module);
    var self = NewDelegate(module, "Recursive.Array.Self");
    CompleteDelegate(self, typeof(void), self.MakeArrayType());
    AddApi(module, "Recursive.Array.Api", ("Use", self));
    self.CreateType();
    assembly.Save(Path.Combine(output, "Recursive.Array.dll"));
}

static void WriteGenericContainerCycle(string output)
{
    var assembly = NewAssembly("Recursive.Container", out var module);
    var self = NewDelegate(module, "Recursive.Container.Self");
    CompleteDelegate(self, typeof(void), typeof(List<>).MakeGenericType(self));
    AddApi(module, "Recursive.Container.Api", ("Use", self));
    self.CreateType();
    assembly.Save(Path.Combine(output, "Recursive.Container.dll"));
}

static void WriteErasedModifierCycle(string output)
{
    var assembly = NewAssembly("Recursive.Modifier", out var module);
    var hidden = module.DefineType(
        "Recursive.Modifier.Hidden",
        TypeAttributes.NotPublic | TypeAttributes.Class | TypeAttributes.Sealed,
        typeof(MulticastDelegate));
    CompleteDelegateWithModifier(hidden, hidden);
    AddApiWithModifier(module, "Recursive.Modifier.Api", hidden);
    hidden.CreateType();
    assembly.Save(Path.Combine(output, "Recursive.Modifier.dll"));
}

static PersistedAssemblyBuilder NewAssembly(string name, out ModuleBuilder module)
{
    var assembly = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
    module = assembly.DefineDynamicModule(name);
    return assembly;
}

static TypeBuilder NewDelegate(ModuleBuilder module, string name) => module.DefineType(
    name,
    TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
    typeof(MulticastDelegate));

static void CompleteDelegate(TypeBuilder type, Type returnType, params Type[] parameters)
{
    var constructor = type.DefineConstructor(
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName,
        CallingConventions.Standard,
        [typeof(object), typeof(IntPtr)]);
    constructor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
    var invoke = type.DefineMethod(
        "Invoke",
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot |
        MethodAttributes.Virtual,
        CallingConventions.Standard,
        returnType,
        parameters);
    invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
}

static void CompleteDelegateWithModifier(TypeBuilder type, Type modifier)
{
    var constructor = type.DefineConstructor(
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName,
        CallingConventions.Standard,
        [typeof(object), typeof(IntPtr)]);
    constructor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
    var invoke = type.DefineMethod(
        "Invoke",
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot |
        MethodAttributes.Virtual,
        CallingConventions.Standard,
        typeof(void),
        null,
        null,
        [typeof(int)],
        null,
        [[modifier]]);
    invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
}

static void AddApi(ModuleBuilder module, string name, params (string Name, Type Parameter)[] methods)
{
    var api = module.DefineType(
        name,
        TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
    foreach (var method in methods)
    {
        var definition = api.DefineMethod(
            method.Name,
            MethodAttributes.Public | MethodAttributes.Static,
            CallingConventions.Standard,
            typeof(void),
            [method.Parameter]);
        definition.GetILGenerator().Emit(OpCodes.Ret);
    }
    api.CreateType();
}

static void AddApiWithModifier(ModuleBuilder module, string name, Type modifier)
{
    var api = module.DefineType(
        name,
        TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
    var method = api.DefineMethod(
        "Use",
        MethodAttributes.Public | MethodAttributes.Static,
        CallingConventions.Standard,
        typeof(void),
        null,
        null,
        [typeof(int)],
        null,
        [[modifier]]);
    method.GetILGenerator().Emit(OpCodes.Ret);
    api.CreateType();
}
