using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void FieldAccessorsSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning field accessors: " + message); }
        var variable = new TypeNode.Tv("type", 0);
        var fieldType = new TypeNode.Fqn("Box", new TypeNode[] { variable });
        var text = new TypeNode.Fqn("kotlin.String");
        var defaultCarrier = DefaultBirCarrier.Create(0, new BirConstant {
            Type = text, Value = new BirLiteral.TextLiteral("preserved") });
        var bytes = ((BirOpaqueBytes)defaultCarrier.Args.Value[2]).Bytes.Value;
        var originalBytes = bytes.ToArray();
        var originals = new[] {
            new BirFunctionDeclaration { Name = "x$field$get", DeclarationId = "collision-get", Params = new[] {
                new BirParameter { Name = "value", Type = text, Attrs = new[] { defaultCarrier } } } },
            new BirFunctionDeclaration { Name = "x$field$get$1", DeclarationId = "collision-get-1" },
            new BirFunctionDeclaration { Name = "x$field$set", DeclarationId = "collision-set" },
            new BirFunctionDeclaration { Name = "authoredRead", DeclarationId = "read", PropertyName = "read", PropertyAccessor = "get" },
            new BirFunctionDeclaration { Name = "authoredWrite", DeclarationId = "write", PropertyName = "write", PropertyAccessor = "set" },
        };
        var originalMethods = originals.Select(method => method.DeepClone()).ToArray();
        BirFieldDeclaration Field(string name) => new() { Name = name, Type = fieldType };
        var mutable = Field("x");
        mutable.KotlinType = new TypeNode.Fqn("SourceBox", new TypeNode[] { variable });
        mutable.NullableFlags = new[] { 1, 0 };
        var fields = new[] { mutable, Field("read"), Field("write"),
            new BirFieldDeclaration { Name = "immutable", Type = fieldType, InitOnly = true },
            new BirFieldDeclaration { Name = "readOnly", Type = fieldType, ReadOnly = true },
            new BirFieldDeclaration { Name = "late", Type = fieldType, Lateinit = true },
            new BirFieldDeclaration { Name = "staticField", Type = fieldType, Static = true },
            new BirFieldDeclaration { Name = "outer", Type = fieldType, Outer = true },
        };
        var fieldSnapshots = fields.Select(field => field.DeepClone()).ToArray();
        var facts = SourceTypeFacts.Empty.MergeNullableFrame(new NullableRepresentationFrame(1, Array.Empty<int>()));
        var definition = new BirClassDefinition { Name = "Owner", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Fields = fields, Methods = originals,
            KotlinSupertypesPre = facts };
        var root = new BirFileDeclaration { FileClass = "File", Types = new IBirTypeDeclaration[] { definition } };
        var bindings = new CurrentDeclarationBindings(new[] { root });
        var indexedMethods = bindings.SourceLocalMethods.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var appended = AddFieldAccessorsSource("Owner", 1, definition, bindings);
        var expected = new[] { "x$field$get$2", "x$field$set$1", "write$field$get", "immutable$field$get",
            "readOnly$field$get", "readOnly$field$set", "late$field$get", "late$field$set" };
        Require(appended.Select(method => method.Name.Value).SequenceEqual(expected)
            && definition.Methods.Value.Length == originals.Length + expected.Length
            && originals.Select((method, index) => ReferenceEquals(method, definition.Methods.Value[index])
                && method.StructurallyEquals(originalMethods[index])).All(value => value)
            && appended.Select((method, index) => ReferenceEquals(method, definition.Methods.Value[originals.Length + index])).All(value => value),
            "append order, authored accessor precedence or collision suffix changed");
        Require(ReferenceEquals(definition.Fields.Value, fields)
            && fields.Select((field, index) => field.StructurallyEquals(fieldSnapshots[index])).All(value => value)
            && ReferenceEquals(definition.KotlinSupertypesPre.Value, facts)
            && ReferenceEquals(((BirOpaqueBytes)defaultCarrier.Args.Value[2]).Bytes.Value, bytes)
            && bytes.AsSpan().SequenceEqual(originalBytes), "field records, source facts or opaque default metadata changed");
        Require(bindings.SourceLocalDeclarations.Count == originals.Length
            && indexedMethods.Count == bindings.SourceLocalMethods.Count
            && indexedMethods.All(pair => bindings.SourceLocalMethods[pair.Key].SequenceEqual(pair.Value)),
            "generated accessors expanded the already-captured method membership");

        static IBirExpression Access(BirFunctionDeclaration method) => method.Body.Value[0] switch {
            BirReturn value => value.Value.Value, BirExpressionStatement value => value.Expr.Value,
            _ => throw new InvalidOperationException("Unexpected accessor body"),
        };
        foreach (var method in appended)
        {
            var identity = KotlinPropertyAccessors.TryIdentity(method, out var property, out var role);
            Require(!method.DeclarationId.IsPresent && !method.TypeParams.IsPresent
                && method.Static.HasValue && !method.Static.Value
                && method.Override.HasValue && !method.Override.Value
                && method.Virtual.HasValue && !method.Virtual.Value
                && method.Abstract.HasValue && !method.Abstract.Value
                && method.ObjectOverride.HasValue && !method.ObjectOverride.Value
                && method.Generated.HasValue && method.Generated.Value && method.Vis.Value == "private"
                && method.PhysicalSlotBridge.Value && method.Attrs.HasValue && method.Attrs.Value.Length == 0
                && !method.KotlinPropertyAccessorCarrier.IsPresent && !method.MethodTypeParameterBoundsPre.IsPresent
                && identity
                && method.PropertyAssociation.Value == "Owner:field:" + property,
                "generated declaration presence or property identity changed");
            var access = (BirGraphValue)Access(method);
            var owner = access switch {
                BirFieldGet value => value.OwnerType.Value,
                BirLateinitGet value => value.OwnerType.Value,
                BirFieldSetExpression value => value.OwnerType.Value, _ => null,
            };
            Require(owner is TypeNode.Fqn { Name: "Owner", Args: [TypeNode.Tv { Scope: "type", I: 0 }] }
                && (role == "get" ? method.Params.Value.Length == 0 && method.Ret.Value == fieldType
                    : method.Params.Value is [{ Name.Value: "value" }] && method.Params.Value[0].Type.Value == fieldType
                        && method.Ret.Value == new TypeNode.Fqn("kotlin.Unit")),
                "accessor signature lost the physical owner frame or storage type");
        }
        Require(Access(appended[0]) is BirFieldGet { Recv.Value: BirThis, Ret.HasValue: true, Sty.IsPresent: false }
            && Access(appended[1]) is BirFieldSetExpression { Recv.Value: BirThis, Value.Value: BirLocal { Name.Value: "value" }, Ret.HasValue: true }
            && Access(appended[6]) is BirLateinitGet { Recv.Value: BirThis, Ret.HasValue: true, Sty.IsPresent: false }
            && !ReferenceEquals(((TypeNode.Fqn)appended[0].Ret.Value).Args, fieldType.Args)
            && !ReferenceEquals(((TypeNode.Fqn)((BirFieldGet)Access(appended[0])).Ret.Value).Args,
                ((TypeNode.Fqn)appended[0].Ret.Value).Args)
            && !ReferenceEquals(((TypeNode.Fqn)appended[1].Params.Value[0].Type.Value).Args,
                ((TypeNode.Fqn)((BirFieldSetExpression)Access(appended[1])).Ret.Value).Args),
            "body kind, explicit return fact or independent type occurrences changed");
        var methodsAfter = definition.Methods.Value;
        Require(AddFieldAccessorsSource("Owner", 1, definition, bindings).Count == 0
            && ReferenceEquals(definition.Methods.Value, methodsAfter), "second preparation appended competing accessors");

        // Validate each generated declaration's actual one-way projection.
        var projected = root.ToDom(bindings.ObserveProjection);
        bindings.BindProjected();
        for (var index = 0; index < appended.Count; index++)
            Require(ReferenceEquals(bindings.GetPhysical(projected["types"][0]["methods"][originals.Length + index].AsObject()).Physical,
                projected["types"][0]["methods"][originals.Length + index]), "generated ID-less method was not captured for its actual projection");

        var absent = new BirClassDefinition { Name = "Absent", Kind = "class" };
        var empty = new BirClassDefinition { Name = "Empty", Kind = "class", Fields = Array.Empty<BirFieldDeclaration>() };
        var emptyRoot = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { absent, empty } };
        var emptyBindings = new CurrentDeclarationBindings(new[] { emptyRoot });
        Require(AddFieldAccessorsSource("Absent", 0, absent, emptyBindings).Count == 0 && !absent.Methods.IsPresent
            && AddFieldAccessorsSource("Empty", 0, empty, emptyBindings).Count == 0
            && empty.Methods.HasValue && empty.Methods.Value.Length == 0,
            "absent fields and an explicitly empty field vector lost their distinct method presence");
        Console.WriteLine("[owning field accessors] self-test OK (append order, roles, collisions, source facts, presence and exact handoff)");
    }
}
