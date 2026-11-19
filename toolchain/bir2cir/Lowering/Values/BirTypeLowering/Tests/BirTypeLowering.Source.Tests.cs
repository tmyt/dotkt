using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class BirTypeLowering
{
    internal static void SourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning physical types: " + message); }
        static TypeNode.Fqn Named(string name, params TypeNode[] arguments) =>
            new(name, arguments.Length == 0 ? null : arguments);
        var integer = Named("kotlin.Int");
        var unit = Named("kotlin.Unit");
        var text = Named("kotlin.String");
        var aliases = new Dictionary<string, string> {
            ["kotlin.Int"] = "System.Int32", ["kotlin.String"] = "System.String",
            ["LogicalAttribute"] = "PhysicalAttribute",
            ["kotlin.collections.List"] = "System.Collections.Generic.IReadOnlyList",
        };
        var markers = new Dictionary<string, IReadOnlyList<TypeNode.Fqn>> { ["Marker"] = new[] { Named("First"), Named("Second") } };
        var app = new PhysicalTypeContext(false, aliases, interfaceMarkers: markers);
        var reference = new PhysicalTypeContext(true, aliases, interfaceMarkers: markers);
        var shadow = new PhysicalTypeContext(false, new Dictionary<string, string>(aliases) {
            ["kotlin.collections.List"] = "System.Collections.Generic.IList",
        });

        var suspend = new TypeNode.Fn(true, new TypeNode.Nullable(integer), new TypeNode[] { text });
        var saved = new TypeNode.Fn(true, new TypeNode.Nullable(new TypeNode.Tv("method", 0)),
            new TypeNode[] { integer });
        var frozen = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> {
            [0] = new TypeNode[] { new TypeNode.Fn(false, unit, new TypeNode[] { integer }) },
        });
        var frozenBytes = frozen.FrozenText;
        var parameter = new BirParameter { Name = "callback", Type = suspend, SuspendFnTypePre = saved,
            KotlinType = saved, NullableGeneric = saved };
        var nullParameter = new BirParameter { Name = "null", Type = OptionalField<TypeNode>.Null,
            SuspendFnType = OptionalField<TypeNode>.Null };
        var field = new BirFieldDeclaration { Name = "field", Type = suspend };
        var property = new BirPropertyDeclaration { Name = "property", Type = suspend,
            GetSig = new TypeNode[] { integer }, GetRet = unit, SetRet = unit };
        var descriptor = new BirMethodImpl { Owner = Named("kotlin.IntArray"), Params = new TypeNode[] { integer },
            Ret = unit };
        var literal = new BirConstant { Type = unit, Value = new BirLiteral.NullLiteral() };
        var attempt = new BirTry { Type = unit, Body = Array.Empty<IBirStatement>() };
        var method = new BirFunctionDeclaration { Name = "ordinary", Ret = suspend, RetSuspendFnTypePre = saved,
            Params = new[] { parameter, nullParameter }, ClrInterfaceImpls = new[] { descriptor },
            MethodTypeParameterBoundsPre = frozen, Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = literal }, attempt }, _physicalDeclarationSignature = true };
        var valueReturn = new BirFunctionDeclaration { Name = "value", Ret = unit, DotktValueReturn = true };
        var ordinaryReturn = new BirFunctionDeclaration { Name = "void", Ret = unit };
        var nothing = new BirFunctionDeclaration { Name = "never", Ret = Named("kotlin.Nothing") };
        var falseNothing = new BirFunctionDeclaration { Name = "false", Ret = unit, RetNothing = false };
        var root = new BirFileDeclaration { Fields = new[] { field }, Properties = new[] { property },
            Methods = new[] { method, valueReturn, ordinaryReturn, nothing, falseNothing } };
        var methods = root.Methods.Value;
        var body = method.Body.Value;
        Apply(root, app);
        Require(ReferenceEquals(root.Methods.Value, methods) && ReferenceEquals(method.Body.Value, body)
            && ReferenceEquals(method.Params.Value[0], parameter) && ReferenceEquals(body[1], attempt),
            "owning declarations, statements or parameter occurrences were copied");
        Require(parameter.Type.Value == Named("object") && field.Type.Value == Named("object")
            && property.Type.Value == Named("object") && method.Ret.Value == Named("object")
            && TypeNodeDataEquality.Equal(parameter.SuspendFnType.Value, saved)
            && TypeNodeDataEquality.Equal(method.RetSuspendFnType.Value, saved)
            && TypeNodeDataEquality.Equal(field.SuspendFnType.Value, suspend)
            && TypeNodeDataEquality.Equal(property.SuspendFnType.Value, suspend)
            && !ReferenceEquals(parameter.SuspendFnType.Value, saved)
            && !ReferenceEquals(((TypeNode.Fn)parameter.SuspendFnType.Value).Params, saved.Params),
            "original suspend metadata birth/order or independent type copying changed");
        Require(!parameter.SuspendFnTypePre.IsPresent && !method.RetSuspendFnTypePre.IsPresent
            && !valueReturn.DotktValueReturn.IsPresent && valueReturn.Ret.Value == unit
            && ordinaryReturn.Ret.Value == Named("void") && nothing.RetNothing.Value
            && falseNothing.RetNothing.HasValue && !falseNothing.RetNothing.Value
            && !ordinaryReturn.RetNothing.IsPresent && nullParameter.Type.IsNull && nullParameter.SuspendFnType.IsNull,
            "consumption, value-return, Nothing or absent/null metadata changed");
        Require(ReferenceEquals(parameter.KotlinType.Value, saved) && ReferenceEquals(parameter.NullableGeneric.Value, saved)
            && ReferenceEquals(method.MethodTypeParameterBoundsPre.Value, frozen) && frozen.FrozenText == frozenBytes
            && method._physicalDeclarationSignature.Value,
            "frozen source metadata or strict-bind transport was changed");
        Require(descriptor.Owner.Value == Named("kotlin.IntArray") && descriptor.Params.Value[0] == Named("System.Int32")
            && descriptor.Ret.Value == Named("void") && literal.Type.Value == Named("void")
            && attempt.Type.Value == Named("void") && property.GetRet.Value == Named("void")
            && property.GetSig.Value[0] == Named("System.Int32"),
            "descriptor/owner/return/Unit-value role changed");

        // Unclassified attr heads are logical. Their structured Fn/FnPtr/Array
        // descendants retain the old field effect rather than whole-type erase.
        var nested = new TypeNode.Fn(true, unit, new TypeNode[] { integer }, Recv: integer,
            Ctx: new TypeNode[] { integer });
        var pointer = new TypeNode.FnPtr(0, 0, 1, unit, new TypeNode[] { integer });
        var attrHead = Named("LogicalAttribute", nested, new TypeNode.Nullable(pointer),
            new TypeNode.Array(integer), integer);
        var attributeArgument = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(1) };
        var forced = new BirAttribute { Attr = attrHead, ArgTypes = new TypeNode[] { integer },
            Args = new IBirAttributeValue[] { attributeArgument } };
        var assemblyOnly = new BirAttribute { Attr = Named("LogicalAttribute"), ArgTypes = new TypeNode[] { integer } };
        var annotationParameter = new BirParameter { Name = "number", Type = integer };
        var implicitCtor = new BirConstructor { Params = new[] { annotationParameter },
            BaseArgs = OptionalField<IBirExpression[]>.Null };
        var thisCtor = new BirConstructor { ThisArgs = Array.Empty<IBirExpression>() };
        var annotation = new BirClassDefinition { Name = "Attribute", Mods = new BirModifiers { Annotation = true },
            Ctors = new[] { implicitCtor, thisCtor }, Interfaces = new TypeNode[] { Named("Marker"), Named("First") } };
        var plainParameter = new BirParameter { Name = "number", Type = integer };
        var refRoot = new BirFileDeclaration { Attrs = new[] { forced }, AssemblyAttrs = new[] { assemblyOnly },
            Types = new IBirTypeDeclaration[] { annotation }, Methods = new[] {
                new BirFunctionDeclaration { Params = new[] { plainParameter }, Ret = unit } } };
        Apply(refRoot, reference);
        var structural = (TypeNode.Fqn)forced.Attr.Value;
        Require(structural.Name == "LogicalAttribute" && structural.Args[0] is TypeNode.Fn {
                Suspend: true, Ret: TypeNode.Fqn { Name: "void" }, Params: [TypeNode.Fqn { Name: "int" }],
                Recv: TypeNode.Fqn { Name: "kotlin.Int" }, Ctx: [TypeNode.Fqn { Name: "kotlin.Int" }] }
            && structural.Args[1] is TypeNode.Nullable { Of: TypeNode.FnPtr {
                Ret: TypeNode.Fqn { Name: "void" }, Params: [TypeNode.Fqn { Name: "int" }] } }
            && structural.Args[2] is TypeNode.Array { Elem: TypeNode.Fqn { Name: "int" } }
            && structural.Args[3] == integer,
            "unclassified type structural domain was broadened or Fn/FnPtr/Array leaf effect was lost");
        Require(forced.ArgTypes.Value[0] == Named("int") && attributeArgument.Type.Value == Named("int")
            && assemblyOnly.ArgTypes.Value[0] == integer && plainParameter.Type.Value == integer
            && annotationParameter.Type.Value == Named("int"),
            "attrs/attribute-class force leaked to a sibling or assemblyAttrs");
        Require(annotation.Base.Value == Named("System.Attribute") && !annotation.Mods.Value.Annotation.IsPresent
            && implicitCtor.BaseArgs.HasValue && implicitCtor.BaseArgs.Value.Length == 0
            && !thisCtor.BaseArgs.IsPresent && thisCtor.ThisArgs.HasValue
            && annotation.Interfaces.Value.SequenceEqual(new TypeNode[] { Named("First"), Named("Second") }),
            "annotation base/delegation presence or stable interface expansion changed");

        BirFileDeclaration ListRoot() => new() { Fields = new[] { new BirFieldDeclaration {
            Name = "list", Type = Named("kotlin.collections.List", integer) } } };
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var actualRoot = ListRoot(); var shadowRoot = ListRoot(); var untouched = ListRoot();
            Apply(shadowRoot, shadow); Apply(actualRoot, app); Apply(untouched, reference);
            Require(actualRoot.Fields.Value[0].Type.Value == Named("System.Collections.Generic.IReadOnlyList", Named("System.Int32"))
                && shadowRoot.Fields.Value[0].Type.Value == Named("System.Collections.Generic.IList", Named("System.Int32"))
                && untouched.Fields.Value[0].Type.Value == Named("kotlin.collections.List", integer),
                "actual/shadow/reference structural invocations inherited the previous context");
        }
        PhysicalProfileSourceSelfTest();
        Console.WriteLine("[owning physical types] self-test OK (field roles, metadata birth, opaque facts, force scope and context isolation)");
    }

    static void PhysicalProfileSourceSelfTest()
    {
        var roles = new List<(string Field, BirPhysicalTypeRole Role)>();
        void Read(BirGraphValue node) => node.RewritePhysicalFieldsInPlace(
            (field, role, value) => { roles.Add((field, role)); return value; },
            (field, role, values) => { roles.Add((field, role)); return values; },
            (_, _, _) => { });
        var type = new TypeNode.Fqn("Probe");
        Read(new BirAttribute { Attr = type, ArgTypes = new TypeNode[] { type } });
        Read(new BirMethodImpl { Owner = type, Params = new TypeNode[] { type }, Ret = type, DotktValueReturn = true });
        Read(new BirNewList { Elem = type });
        Read(new BirNewArray { Elem = type });
        Read(new BirNewDelegate { FuncType = type });
        Read(new BirConvert { To = type });
        var expected = new[] { ("attr", BirPhysicalTypeRole.Structural), ("argTypes", BirPhysicalTypeRole.Value),
            ("owner", BirPhysicalTypeRole.Owner), ("params", BirPhysicalTypeRole.Value), ("ret", BirPhysicalTypeRole.Value),
            ("elem", BirPhysicalTypeRole.GenericArgument), ("elem", BirPhysicalTypeRole.Value),
            ("funcType", BirPhysicalTypeRole.Delegate), ("to", BirPhysicalTypeRole.Value) };
        if (!roles.SequenceEqual(expected)) throw new InvalidOperationException("Generated physical field roles changed");
    }
}
