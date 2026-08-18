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
assembly.Save(output);

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
