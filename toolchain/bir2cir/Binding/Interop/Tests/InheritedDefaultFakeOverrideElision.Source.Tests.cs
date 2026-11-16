using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class InheritedDefaultFakeOverrideElision
{
    internal static void SelfTestSource()
    {
        var text = new TypeNode.Fqn("kotlin.String");
        var variable = new TypeNode.Tv("method", 0);
        BirFunctionDeclaration Fake(string name) => new() {
            Name = name, DeclarationId = name, FakeOverride = true, Abstract = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Params = new[] { new BirParameter { Name = "value", Type = variable } }, Ret = variable,
            Body = Array.Empty<IBirStatement>(),
            InheritedImplementation = new BirInheritedImplementation {
                Owner = new TypeNode.Fqn("Selected.Base", new TypeNode[] { new TypeNode.Tv("type", 1) }),
                Member = name, Kind = "method", Arity = 1,
                TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { text },
                    SpecialConstraints = new[] { "class" } } },
                Sig = new TypeNode[] { variable },
                MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [0] = new TypeNode[] { text } }),
            },
        };
        BirFunctionDeclaration Accessor(string name, string association, string role)
        {
            var method = Fake(name);
            method.PropertyName = "value"; method.PropertyAssociation = association; method.PropertyAccessor = role;
            return method;
        }
        BirPropertyDeclaration Property(string association, params string[] roles) => new() {
            Name = "value", Type = text, PropertyAssociation = association, KotlinAccessors = roles,
        };
        var first = Fake("first");
        var second = Fake("second");
        var keep = Fake("keep"); keep.FakeOverride = false;
        var missingImplementation = Fake("missing"); missingImplementation.InheritedImplementation = OptionalField<BirInheritedImplementation>.Null;
        var partialGet = Accessor("partialGet", "partial", "get");
        var removedGet = Accessor("removedGet", "removed", "get");
        var removedSet = Accessor("removedSet", "removed", "set");
        var methods = new[] { first, keep, partialGet, second, removedGet, missingImplementation, removedSet };
        var partial = Property("partial", "get", "set");
        var partialRoles = partial.KotlinAccessors.Value;
        var unrelated = Property("unrelated", "get", "set");
        var removed = Property("removed", "get", "set");
        var preexisting = new BirInheritedDefaultMethod { Member = "existing", Params = Array.Empty<TypeNode>(), Ret = text };
        var type = new BirInterfaceDefinition { Name = "Leaf", Kind = "interface", Methods = methods,
            TypeParams = new[] { new BirTypeParameter { Name = "A" }, new BirTypeParameter { Name = "B" } },
            Properties = new[] { partial, unrelated, removed }, InheritedDefaultMethods = new[] { preexisting } };
        var shadowedMethod = Fake("shadowed");
        var shadowed = new BirInterfaceDefinition { Name = "Shadow", Kind = "interface", Methods = new[] { shadowedMethod } };
        var shadowing = new BirClassDefinition { Name = "Shadow", Kind = "class", Abstract = true, Methods = new[] { Fake("class") } };
        var absentMethods = new BirInterfaceDefinition { Name = "Absent", Kind = "interface",
            InheritedDefaultMethods = OptionalField<BirInheritedDefaultMethod[]>.Null };
        var owners = new IBirTypeDeclaration[] { shadowed, type, shadowing, absentMethods };
        var file = new BirFileDeclaration { FileClass = "Probe", Types = owners };
        var retired = new List<BirFunctionDeclaration>();
        Apply(file, retired.Add);
        var expectedRetirements = new[] { removedSet, removedGet, second, partialGet, first };
        if (retired.Count != expectedRetirements.Length
            || !retired.Zip(expectedRetirements, (actual, expected) => ReferenceEquals(actual, expected)).All(same => same)
            || type.Methods.Value.Length != 2 || !ReferenceEquals(type.Methods.Value[0], keep)
            || !ReferenceEquals(type.Methods.Value[1], missingImplementation)
            || !ReferenceEquals(file.Types.Value, owners) || !ReferenceEquals(owners[1], type)
            || !ReferenceEquals(methods[0], first) || !ReferenceEquals(methods[6], removedSet)
            || !ReferenceEquals(shadowed.Methods.Value[0], shadowedMethod)
            || shadowing.Methods.Value[0].InheritedImplementation.Value.Member.Value != "class"
            || absentMethods.Methods.IsPresent || !absentMethods.InheritedDefaultMethods.IsNull)
            throw new InvalidOperationException("Typed inherited elision changed selection/order, original vectors or explicit presence");
        var rows = type.InheritedDefaultMethods.Value;
        if (rows.Length != 3 || !ReferenceEquals(rows[0], preexisting)
            || rows[1].Member.Value != "second" || rows[2].Member.Value != "first"
            || !TypeNodeDataEquality.Equal(rows[1].Params.Value[0], variable)
            || !TypeNodeDataEquality.Equal(rows[1].Ret.Value, variable)
            || ReferenceEquals(rows[1].Implementation.Value, second.InheritedImplementation.Value)
            || !rows[1].Implementation.Value.StructurallyEquals(second.InheritedImplementation.Value)
            || rows[1].Overrides.IsPresent || rows[1].NullableGenericRet.IsPresent)
            throw new InvalidOperationException("Inherited method rows lost append order or reconstructed the selected implementation facts");
        second.InheritedImplementation.Value.Member = "changed after elision";
        if (rows[1].Implementation.Value.Member.Value != "second"
            || type.Properties.Value.Length != 2 || !ReferenceEquals(type.Properties.Value[0], partial)
            || !partial.KotlinAccessors.Value.SequenceEqual(new[] { "set" })
            || !partialRoles.SequenceEqual(new[] { "get", "set" })
            || !ReferenceEquals(type.Properties.Value[1], unrelated)
            || !unrelated.KotlinAccessors.Value.SequenceEqual(new[] { "get", "set" }))
            throw new InvalidOperationException("Inherited accessor elision lost independent facts, partial roles or association isolation");
        var decoded = BirGraphDom.ReadBirInterfaceDefinition(type.ToDom());
        if (!type.StructurallyEquals(decoded) || decoded.InheritedDefaultMethods.Value.Length != 3)
            throw new InvalidOperationException("Interface inherited method facts were dropped by the maintained typed reader");
        var noChanges = new List<BirFunctionDeclaration>();
        var remainingMethods = type.Methods.Value;
        var remainingProperties = type.Properties.Value;
        Apply(file, noChanges.Add);
        if (noChanges.Count != 0 || !ReferenceEquals(type.Methods.Value, remainingMethods)
            || !ReferenceEquals(type.Properties.Value, remainingProperties) || type.InheritedDefaultMethods.Value.Length != 3)
            throw new InvalidOperationException("Repeated inherited elision copied live declarations or duplicated saved rows");
        Console.WriteLine("[typed inherited elision] self-test OK (selected facts, reverse retirement/append, partial roles, last writer and owning identity)");
    }
}
