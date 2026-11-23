using System.Reflection;
using System.Reflection.Emit;

var output = Path.GetFullPath(args[0]);
var assembly = new PersistedAssemblyBuilder(
    new AssemblyName("TransitiveSlotProbe"),
    typeof(object).Assembly);
var module = assembly.DefineDynamicModule("TransitiveSlotProbe");

var publicInterface = module.DefineType(
    "TransitiveSlotProbe.IPublic",
    TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
publicInterface.DefineMethod(
    "Read",
    MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual |
        MethodAttributes.NewSlot | MethodAttributes.HideBySig,
    typeof(int),
    Type.EmptyTypes);
var publicInterfaceType = publicInterface.CreateType();

var hiddenInterface = module.DefineType(
    "TransitiveSlotProbe.IHidden",
    TypeAttributes.NotPublic | TypeAttributes.Interface | TypeAttributes.Abstract);
hiddenInterface.AddInterfaceImplementation(publicInterfaceType);
var hiddenInterfaceType = hiddenInterface.CreateType();

var carrier = module.DefineType(
    "TransitiveSlotProbe.Carrier",
    TypeAttributes.Public | TypeAttributes.Class);
carrier.AddInterfaceImplementation(hiddenInterfaceType);
carrier.DefineDefaultConstructor(MethodAttributes.Public);
var read = carrier.DefineMethod(
    "Read",
    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot |
        MethodAttributes.HideBySig,
    typeof(int),
    Type.EmptyTypes);
var il = read.GetILGenerator();
il.Emit(OpCodes.Ldc_I4, 97);
il.Emit(OpCodes.Ret);
carrier.CreateType();

var publicGeneric = module.DefineType(
    "TransitiveSlotProbe.IPublicGeneric`1",
    TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
var publicGenericParameter = publicGeneric.DefineGenericParameters("T")[0];
publicGeneric.DefineMethod(
    "Echo",
    MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual |
        MethodAttributes.NewSlot | MethodAttributes.HideBySig,
    publicGenericParameter,
    [publicGenericParameter]);
var publicGenericType = publicGeneric.CreateType();

var hiddenGeneric = module.DefineType(
    "TransitiveSlotProbe.IHiddenGeneric`1",
    TypeAttributes.NotPublic | TypeAttributes.Interface | TypeAttributes.Abstract);
var hiddenGenericParameter = hiddenGeneric.DefineGenericParameters("T")[0];
hiddenGeneric.AddInterfaceImplementation(publicGenericType.MakeGenericType(hiddenGenericParameter));
var hiddenGenericType = hiddenGeneric.CreateType();

var genericCarrier = module.DefineType(
    "TransitiveSlotProbe.GenericCarrier",
    TypeAttributes.Public | TypeAttributes.Class);
genericCarrier.AddInterfaceImplementation(hiddenGenericType.MakeGenericType(typeof(string)));
genericCarrier.DefineDefaultConstructor(MethodAttributes.Public);
var echo = genericCarrier.DefineMethod(
    "Echo",
    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot |
        MethodAttributes.HideBySig,
    typeof(string),
    [typeof(string)]);
var echoIl = echo.GetILGenerator();
echoIl.Emit(OpCodes.Ldarg_1);
echoIl.Emit(OpCodes.Ret);
genericCarrier.CreateType();

var publicExplicit = module.DefineType(
    "TransitiveSlotProbe.IPublicExplicit",
    TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
publicExplicit.DefineMethod(
    "ReadExplicit",
    MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual |
        MethodAttributes.NewSlot | MethodAttributes.HideBySig,
    typeof(int),
    Type.EmptyTypes);
var publicExplicitType = publicExplicit.CreateType();

var hiddenExplicit = module.DefineType(
    "TransitiveSlotProbe.IHiddenExplicit",
    TypeAttributes.NotPublic | TypeAttributes.Interface | TypeAttributes.Abstract);
hiddenExplicit.AddInterfaceImplementation(publicExplicitType);
var hiddenExplicitType = hiddenExplicit.CreateType();

var explicitCarrier = module.DefineType(
    "TransitiveSlotProbe.ExplicitCarrier",
    TypeAttributes.Public | TypeAttributes.Class);
explicitCarrier.AddInterfaceImplementation(hiddenExplicitType);
explicitCarrier.DefineDefaultConstructor(MethodAttributes.Public);
var explicitBody = explicitCarrier.DefineMethod(
    "TransitiveSlotProbe.IPublicExplicit.ReadExplicit",
    MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.Virtual |
        MethodAttributes.NewSlot | MethodAttributes.HideBySig,
    typeof(int),
    Type.EmptyTypes);
var explicitIl = explicitBody.GetILGenerator();
explicitIl.Emit(OpCodes.Ldc_I4, 98);
explicitIl.Emit(OpCodes.Ret);
explicitCarrier.DefineMethodOverride(explicitBody, publicExplicitType.GetMethod("ReadExplicit")!);
explicitCarrier.CreateType();

var publicDerived = module.DefineType(
    "TransitiveSlotProbe.IPublicDerived",
    TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
publicDerived.AddInterfaceImplementation(publicExplicitType);
var publicDerivedType = publicDerived.CreateType();

var publicDerivedCarrier = module.DefineType(
    "TransitiveSlotProbe.PublicDerivedCarrier",
    TypeAttributes.Public | TypeAttributes.Class);
publicDerivedCarrier.AddInterfaceImplementation(publicDerivedType);
publicDerivedCarrier.DefineDefaultConstructor(MethodAttributes.Public);
var publicDerivedBody = publicDerivedCarrier.DefineMethod(
    "TransitiveSlotProbe.IPublicExplicit.ReadExplicit",
    MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.Virtual |
        MethodAttributes.NewSlot | MethodAttributes.HideBySig,
    typeof(int),
    Type.EmptyTypes);
var publicDerivedIl = publicDerivedBody.GetILGenerator();
publicDerivedIl.Emit(OpCodes.Ldc_I4, 99);
publicDerivedIl.Emit(OpCodes.Ret);
publicDerivedCarrier.DefineMethodOverride(
    publicDerivedBody,
    publicExplicitType.GetMethod("ReadExplicit")!);
publicDerivedCarrier.CreateType();

// CLR event accessors may independently supply a default body or require an implementation.
foreach (var shape in new[] { ("IAllDefaultEvent", false, false), ("IAddAbstractEvent", true, false),
    ("IRemoveAbstractEvent", false, true), ("IAllAbstractEvent", true, true) })
{
    var iface = module.DefineType("TransitiveSlotProbe." + shape.Item1,
        TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
    MethodBuilder Accessor(string name, bool isAbstract)
    {
        var flags = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot |
            MethodAttributes.SpecialName | MethodAttributes.HideBySig;
        if (isAbstract) flags |= MethodAttributes.Abstract;
        var method = iface.DefineMethod(name, flags, typeof(void), [typeof(EventHandler)]);
        if (!isAbstract) method.GetILGenerator().Emit(OpCodes.Ret);
        return method;
    }
    var ev = iface.DefineEvent("Changed", EventAttributes.None, typeof(EventHandler));
    ev.SetAddOnMethod(Accessor("add_Changed", shape.Item2));
    ev.SetRemoveOnMethod(Accessor("remove_Changed", shape.Item3));
    iface.CreateType();
}
assembly.Save(output);
WritePropertySlots(args[1], args[2]);

// This fixture is consumed through the SDK reference universe, not Private.CoreLib.
static void WritePropertySlots(string path, string refPack)
{
    using var context = new MetadataLoadContext(new PathAssemblyResolver(Directory.GetFiles(refPack, "*.dll")), "System.Runtime");
    var asm = new PersistedAssemblyBuilder(new AssemblyName("NativePropertySlots"), context.CoreAssembly);
    var mod = asm.DefineDynamicModule("NativePropertySlots");
    var integer = context.CoreAssembly.GetType("System.Int32", throwOnError: true)!;
    const MethodAttributes slot = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.SpecialName;
    var face = mod.DefineType("PropertySlots.I", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
    var read = face.DefineMethod("ReadValue", slot | MethodAttributes.Abstract, integer, Type.EmptyTypes);
    face.DefineProperty("Value", PropertyAttributes.None, integer, Type.EmptyTypes).SetGetMethod(read);
    var owner = mod.DefineType("PropertySlots.Base", TypeAttributes.Public);
    owner.AddInterfaceImplementation(face.CreateType());
    owner.DefineDefaultConstructor(MethodAttributes.Public);
    var getter = owner.DefineMethod("FetchValue", slot, integer, Type.EmptyTypes);
    var il = getter.GetILGenerator();
    il.Emit(OpCodes.Ldc_I4_1);
    il.Emit(OpCodes.Ret);
    owner.DefineProperty("Value", PropertyAttributes.None, integer, Type.EmptyTypes).SetGetMethod(getter);
    owner.DefineMethodOverride(getter, read);
    owner.CreateType();
    asm.Save(path);
}

var loaded = Assembly.LoadFile(output);
var loadedInterface = loaded.GetType("TransitiveSlotProbe.IPublic")!;
var loadedCarrier = loaded.GetType("TransitiveSlotProbe.Carrier")!;
var instance = Activator.CreateInstance(loadedCarrier)!;
if (!loadedInterface.IsAssignableFrom(loadedCarrier) ||
    !Equals(97, loadedInterface.GetMethod("Read")!.Invoke(instance, null)))
    throw new InvalidOperationException("generated transitive interface metadata does not satisfy its public slot");
var loadedGenericInterface = loaded.GetType("TransitiveSlotProbe.IPublicGeneric`1")!
    .MakeGenericType(typeof(string));
var loadedGenericCarrier = loaded.GetType("TransitiveSlotProbe.GenericCarrier")!;
var genericInstance = Activator.CreateInstance(loadedGenericCarrier)!;
if (!loadedGenericInterface.IsAssignableFrom(loadedGenericCarrier) ||
    !Equals("ok", loadedGenericInterface.GetMethod("Echo")!.Invoke(genericInstance, ["ok"])))
    throw new InvalidOperationException("generated generic transitive metadata does not satisfy its public slot");
var loadedExplicitInterface = loaded.GetType("TransitiveSlotProbe.IPublicExplicit")!;
var loadedExplicitCarrier = loaded.GetType("TransitiveSlotProbe.ExplicitCarrier")!;
var explicitInstance = Activator.CreateInstance(loadedExplicitCarrier)!;
if (!loadedExplicitInterface.IsAssignableFrom(loadedExplicitCarrier) ||
    !Equals(98, loadedExplicitInterface.GetMethod("ReadExplicit")!.Invoke(explicitInstance, null)))
    throw new InvalidOperationException("generated transitive explicit metadata does not satisfy its public slot");
var loadedPublicDerivedCarrier = loaded.GetType("TransitiveSlotProbe.PublicDerivedCarrier")!;
var publicDerivedInstance = Activator.CreateInstance(loadedPublicDerivedCarrier)!;
if (!loadedExplicitInterface.IsAssignableFrom(loadedPublicDerivedCarrier) ||
    !Equals(99, loadedExplicitInterface.GetMethod("ReadExplicit")!.Invoke(publicDerivedInstance, null)))
    throw new InvalidOperationException("generated public-derived metadata does not satisfy its base slot");

var runtime = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("EventSlotControl"), AssemblyBuilderAccess.Run);
var runtimeModule = runtime.DefineDynamicModule("EventSlotControl");
foreach (var iface in loaded.GetTypes().Where(type => type.Name.EndsWith("Event")))
{
    var methods = iface.GetMethods();
    Type Make(string suffix, bool implement)
    {
        var type = runtimeModule.DefineType(iface.Name + suffix, TypeAttributes.Public | TypeAttributes.Sealed);
        type.AddInterfaceImplementation(iface);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        if (implement)
            foreach (var declaration in methods.Where(method => method.IsAbstract))
            {
                var body = type.DefineMethod(declaration.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final |
                    MethodAttributes.HideBySig | MethodAttributes.SpecialName, typeof(void), [typeof(EventHandler)]);
                body.GetILGenerator().Emit(OpCodes.Ret);
                type.DefineMethodOverride(body, declaration);
            }
        return type.CreateType();
    }
    var correct = Activator.CreateInstance(Make("Implemented", true));
    foreach (var method in methods) method.Invoke(correct, [null]);
    var missingRejected = false;
    try { Activator.CreateInstance(Make("Missing", false)); }
    catch (TypeLoadException) { missingRejected = true; }
    if (missingRejected != methods.Any(method => method.IsAbstract))
        throw new InvalidOperationException("generated default/abstract event slots disagree with CLR requirements");
}
