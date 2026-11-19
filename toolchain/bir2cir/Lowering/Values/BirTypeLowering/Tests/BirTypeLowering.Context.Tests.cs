using DotKt.Bir;

static partial class BirTypeLowering
{
    internal static void PhysicalContextSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Physical type context: " + message); }
        static TypeNode.Fqn Named(string name, params TypeNode[] arguments) =>
            new(name, arguments.Length == 0 ? null : arguments);
        var integer = Named("kotlin.Int");
        var unit = Named("kotlin.Unit");
        var text = Named("System.String");
        var aliases = new Dictionary<string, string> {
            ["kotlin.Int"] = "System.Int32", ["kotlin.Any"] = "System.Object",
            ["kotlin.collections.List"] = "System.Collections.Generic.IReadOnlyList",
            ["AliasedFrame"] = "System.Collections.Generic.List",
        };
        var shadowAliases = new Dictionary<string, string>(aliases) {
            ["kotlin.collections.List"] = "System.Collections.Generic.IList",
        };
        var names = new Dictionary<string, string> {
            ["External.Inner"] = "External`1+Inner", ["Local"] = "Shipped.Local",
        };
        var locals = new HashSet<string> { "Local" };
        var frames = new Dictionary<string, NullableRepresentationFrame> {
            ["AliasedFrame"] = new(1, new[] { 0 }, physicalOrder: new[] { 1, 0 }),
        };
        var markers = new Dictionary<string, IReadOnlyList<TypeNode.Fqn>> { ["Marker"] = new[] { Named("PhysicalMarker") } };
        bool Value(TypeNode.Fqn type) => type.Name is "kotlin.Int" or "System.Int32" or "System.DateTime";
        var actual = new PhysicalTypeContext(false, aliases, Value, names, locals, frames, markers, "actual.kt");
        var shadow = new PhysicalTypeContext(false, shadowAliases, Value, names, locals, frames, markers, "shadow.kt");
        var reference = new PhysicalTypeContext(true, aliases, Value, names, locals, frames, markers, "reference.kt");
        var empty = new PhysicalTypeContext(false, file: "empty.kt");
        Require(ReferenceEquals(actual.Aliases, aliases) && ReferenceEquals(actual.PhysicalNames, names)
            && ReferenceEquals(actual.LocalNames, locals) && ReferenceEquals(actual.NullableFrames, frames)
            && ReferenceEquals(actual.InterfaceMarkers, markers), "context recaptured module facts");

        var list = Named("kotlin.collections.List", integer);
        var actualList = Named("System.Collections.Generic.IReadOnlyList", Named("System.Int32"));
        var shadowList = Named("System.Collections.Generic.IList", Named("System.Int32"));
        // Deliberately interleave all invocations: no prior file, shadow or force
        // branch may seed a later low-context scalar call.
        for (var iteration = 0; iteration < 2; iteration++)
        {
            Require(LowerSlot(list, actual, PhysicalSlotRole.Value) == actualList
                && LowerSlot(list, shadow, PhysicalSlotRole.Value) == shadowList
                && LowerSlot(integer, reference, PhysicalSlotRole.Value) == integer
                && LowerSlot(integer, reference, PhysicalSlotRole.Value, force: true) == Named("int")
                && LowerSlot(integer, empty, PhysicalSlotRole.Value) == integer
                && LowerSlot(list, actual, PhysicalSlotRole.Value) == actualList,
                "actual/shadow/reference/forced contexts leaked into each other");
            Require(LowerSlot(Named("External.Inner"), actual, PhysicalSlotRole.Value) == Named("External`1+Inner")
                && LowerSlot(Named("Local"), actual, PhysicalSlotRole.Value) == Named("Local")
                && LowerSlot(Named("External.Inner"), empty, PhysicalSlotRole.Value) == Named("External.Inner")
                && PhysicalHead("External.Inner", null, null) == Named("External.Inner"),
                "local-name priority or default-empty head naming inherited another invocation");
        }
        Require(LowerPhysicalType(list, aliases, Value, names, false, locals, nullableFrames: frames) == actualList
            && LowerPhysicalType(list, shadowAliases, Value, names, false, locals, nullableFrames: frames) == shadowList,
            "existing explicit wrapper diverged from the owning entry");
        Require(LowerSlot(new TypeNode.Nullable(integer), actual, PhysicalSlotRole.Value) == new TypeNode.Nullable(Named("System.Int32"))
            && LowerSlot(new TypeNode.Nullable(text), actual, PhysicalSlotRole.Value) == text
            && LowerSlot(new TypeNode.Nullable(integer), empty, PhysicalSlotRole.Value) == integer,
            "nullable value oracle was inherited or changed the reference fold");
        var framed = Named("AliasedFrame", Named("System.Object"), text);
        Require(LowerSlot(framed, actual, PhysicalSlotRole.Value) == Named("System.Collections.Generic.List", text)
            && framed.Args!.Length == 2 && framed.Args[1] == text,
            "ordinary owner argument projection changed the input frame");

        var suspend = new TypeNode.Fn(true, unit, Array.Empty<TypeNode>());
        var explicitValue = new TypeNode.Fn(false, unit, Array.Empty<TypeNode>(), Clr: "System.Func");
        Require(LowerSlot(unit, actual, PhysicalSlotRole.Return) == Named("void")
            && LowerSlot(unit, actual, PhysicalSlotRole.Value) == unit
            && LowerSlot(suspend, actual, PhysicalSlotRole.Value) == Named("object")
            && LowerSlot(suspend, actual, PhysicalSlotRole.Delegate) is TypeNode.Fn {
                Suspend: false, Ret: TypeNode.Fqn { Name: "void" }, Clr: "System.Action" }
            && LowerSlot(explicitValue, actual, PhysicalSlotRole.Delegate) is TypeNode.Fn {
                Ret: TypeNode.Fqn { Name: "kotlin.Unit" }, Clr: "System.Func" },
            "return/value/delegate entry roles lost the Unit or suspend ABI");
        var primitiveArray = Named("kotlin.IntArray");
        Require(LowerSlot(primitiveArray, actual, PhysicalSlotRole.Owner) == primitiveArray
            && LowerSlot(primitiveArray, actual, PhysicalSlotRole.Value) == new TypeNode.Array(Named("System.Int32")),
            "owner-role primitive arrays were decomposed like value storage");

        var pointer = Named(PointerIntrinsicFqn, integer);
        Require(LowerSlot(pointer, actual, PhysicalSlotRole.Value) == new TypeNode.Ptr(Named("System.Int32")),
            "native pointer re-entry did not use this invocation's aliases");
        try
        {
            LowerSlot(pointer, shadow, PhysicalSlotRole.GenericArgument);
            throw new InvalidOperationException("direct pointer generic argument was accepted");
        }
        catch (InvalidDataException failure)
        { Require(failure.Message.Contains("shadow.kt", StringComparison.Ordinal), "pointer refusal inherited another file"); }
        var callback = Named(FunctionPointerVocabulary.Pointer, Named(FunctionPointerVocabulary.Conventions[0]),
            new TypeNode.Fn(false, unit, new TypeNode[] {
                Named(FunctionPointerVocabulary.Arrays[2], Named(FunctionPointerVocabulary.Optional, Named("Marker"), integer)) }));
        Require(LowerSlot(callback, actual, PhysicalSlotRole.Value) is TypeNode.FnPtr {
            Ret: TypeNode.Fqn { Name: "void" }, Params: [TypeNode.Array { Rank: 2, SzArray: false,
                Elem: TypeNode.Mod { Req: false, Of: TypeNode.Fqn { Name: "System.Int32" } } }] },
            "native function-pointer/modifier/ranked-array recursion bypassed the context");
        var wide = new TypeNode.Fn(false, unit, Enumerable.Repeat<TypeNode>(text, CanonicalDelegateMaxArity + 1).ToArray());
        try
        {
            LowerSlot(wide, reference, PhysicalSlotRole.Delegate);
            throw new InvalidOperationException("wide function unexpectedly acquired a CLR delegate");
        }
        catch (InvalidOperationException failure) when (failure.Message.Contains("has no CLR delegate", StringComparison.Ordinal))
        { Require(failure.Message.Contains("reference.kt", StringComparison.Ordinal), "delegate refusal inherited another file"); }
        Console.WriteLine("[physical type context] self-test OK (actual/shadow/ref/force isolation, positions, aliases, frames, native slots and diagnostics)");
    }
}
