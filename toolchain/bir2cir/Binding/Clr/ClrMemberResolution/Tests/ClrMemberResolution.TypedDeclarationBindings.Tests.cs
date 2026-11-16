using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void DeclarationBindingsSelfTest()
    {
        var priorReferences = _refs;
        var priorLocalTypes = _localTypes;
        try
        {
            var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            var integer = new TypeNode.Fqn("System.Int32");
            var text = new TypeNode.Fqn("System.String");
            var obj = new TypeNode.Fqn("System.Object");
            var boolean = new TypeNode.Fqn("System.Boolean");
            var bytes = new TypeNode.Array(new TypeNode.Fqn("System.Byte"));
            BirFunctionDeclaration Method(string name, TypeNode result, params TypeNode[] parameters)
            {
                BirLiteral literal = result == boolean ? new BirLiteral.BooleanLiteral(true)
                    : result == text ? new BirLiteral.TextLiteral("bound") : new BirLiteral.IntegerLiteral(0);
                return new() {
                    Name = name, Static = false, Override = false, Virtual = true,
                    Params = parameters.Select((type, index) => new BirParameter { Name = "p" + index, Type = type }).ToArray(),
                    Ret = result, Body = new IBirStatement[] {
                        new BirReturn { Value = new BirConstant { Type = result, Value = literal } },
                    },
                };
            }
            BirClassDefinition Owner(string name, TypeNode parent, params BirFunctionDeclaration[] methods) => new() {
                Name = name, Kind = "class", Base = parent, Methods = methods,
            };
            BirMethodImpl Slot(TypeNode owner, string member, TypeNode result, params TypeNode[] parameters) => new() {
                Owner = owner, Member = member, Arity = 0, Params = parameters, Ret = result,
            };

            var read = Method("Read", integer, bytes, integer, integer);
            read.DeclarationId = "external-read";
            var wrongRead = Method("Read", integer, new TypeNode.Fqn("System.Byte"), integer, integer);
            wrongRead.Override = OptionalField<bool>.Null;
            var staticRead = Method("Read", integer, bytes, integer, integer);
            staticRead.Static = true; staticRead.Virtual = false;
            var retainedRead = Method("Read", integer, bytes, integer, integer);
            retainedRead.Override = true;
            var stream = new TypeNode.Fqn("System.IO.MemoryStream");
            var external = Owner("Fixture.External", stream, read);
            var mismatch = Owner("Fixture.Mismatch", stream, wrongRead);
            var statik = Owner("Fixture.Static", stream, staticRead);
            var retained = Owner("Fixture.Retained", stream, retainedRead);

            var comparer = new TypeNode.Fqn("System.Collections.Generic.Comparer", new TypeNode[] { integer });
            var compare = Slot(comparer, "Compare", integer, integer, integer);
            compare.ReferenceMember = "Compare";
            var inherited = Slot(comparer, "Equals", boolean, obj);
            var compareMethod = Method("Compare", integer, integer, integer);
            compareMethod.ClrBaseImpls = new[] { compare };
            var equalsMethod = Method("Equals", boolean, obj);
            equalsMethod.ClrBaseImpls = new[] { inherited };
            var generic = Owner("Fixture.Generic", comparer, compareMethod, equalsMethod);

            var stringSlot = Slot(obj, "ToString", text);
            stringSlot.ReferenceMember = OptionalField<string>.Null;
            stringSlot.MemberRef = OptionalField<BirMemberReference>.Null;
            var toString = Method("ToString", text);
            toString.ClrBaseImpls = new[] { stringSlot };
            var stringer = Owner("Fixture.Stringer", obj, toString);
            var localBase = Owner("Fixture.LocalBase", obj, Method("Local", integer));
            var localSlot = Slot(new TypeNode.Fqn("Fixture.LocalBase"), "Local", integer);
            var localMethod = Method("Local", integer); localMethod.Override = true;
            localMethod.ClrBaseImpls = new[] { localSlot };
            var local = Owner("Fixture.Local", new TypeNode.Fqn("Fixture.LocalBase"), localMethod);
            var owners = new IBirTypeDeclaration[] { external, mismatch, statik, retained, generic, stringer, localBase, local };
            var file = new BirFileDeclaration { FileClass = "DeclarationBindings", Types = owners };
            var localTypes = owners.Select(type => type.Name.Value).ToHashSet(StringComparer.Ordinal);
            var methods = external.Methods.Value;
            var parameters = read.Params.Value;
            var body = read.Body.Value;
            var firstParameter = parameters[0];
            var descriptorParameters = compare.Params.Value;
            var descriptors = compareMethod.ClrBaseImpls.Value;
            ApplyDeclarationBindings(file, refs, localTypes);

            if (!read.Override.OrDefault() || !retainedRead.Override.OrDefault()
                || !wrongRead.Override.IsNull || staticRead.Override.OrDefault()
                || !compareMethod.Override.OrDefault() || !equalsMethod.Override.OrDefault()
                || !ReferenceEquals(file.Types.Value, owners) || !ReferenceEquals(external.Methods.Value, methods)
                || !ReferenceEquals(methods[0], read) || !ReferenceEquals(read.Params.Value, parameters)
                || read.Name.Value != "Read" || read.Static.OrDefault() || read.DeclarationId.Value != "external-read"
                || !ReferenceEquals(read.Body.Value, body)
                || !ReferenceEquals(parameters[0], firstParameter) || !ReferenceEquals(read.Ret.Value, integer)
                || !ReferenceEquals(compareMethod.ClrBaseImpls.Value, descriptors)
                || !ReferenceEquals(descriptors[0], compare) || !ReferenceEquals(compare.Params.Value, descriptorParameters))
                throw new InvalidOperationException("Typed declaration binding changed ownership/signatures or weakened exact external-override matching");

            var reference = compare.MemberRef.Value;
            if (compare.ReferenceMember.IsPresent || !ReferenceEquals(compare.Owner.Value, comparer)
                || reference.DeclaringType.Value is not TypeNode.Fqn { Name: "System.Collections.Generic.Comparer`1", Args: [TypeNode.Fqn { Name: "System.Int32" }] }
                || reference.ParameterTypes.Value is not [TypeNode.Tv { Scope: "type", I: 0 }, TypeNode.Tv { Scope: "type", I: 0 }]
                || reference.ReturnType.Value is not TypeNode.Fqn { Name: "System.Int32" }
                || reference.Name.Value != "Compare" || reference.Kind.Value != MemberRefNode.Kinds.Method
                || reference.GenericArity.Value != 0 || reference.CallingConvention.Value != MemberRefNode.Instance
                || reference.Assembly.Value != typeof(object).Assembly.GetName().Name)
                throw new InvalidOperationException("Typed MethodImpl lost its exact open declaration and constructed owner frame");
            if (inherited.MemberRef.Value.DeclaringType.Value is not TypeNode.Fqn { Name: "System.Object" }
                || inherited.MemberRef.Value.ParameterTypes.Value is not [TypeNode.Fqn { Name: "System.Object" }]
                || inherited.MemberRef.Value.ReturnType.Value is not TypeNode.Fqn { Name: "System.Boolean" }
                || stringSlot.ReferenceMember.IsPresent || !stringSlot.MemberRef.HasValue
                || stringSlot.MemberRef.Value.ParameterTypes.Value.Length != 0
                || stringSlot.MemberRef.Value.Name.Value != "ToString" || localSlot.MemberRef.IsPresent)
                throw new InvalidOperationException("Typed MethodImpl changed inherited declaring identity, empty/null presence or local-slot exclusion");

            var stable = file.DeepClone();
            ApplyDeclarationBindings(file, refs, localTypes);
            if (!file.StructurallyEquals(stable) || !ReferenceEquals(compare.MemberRef.Value, reference))
                throw new InvalidOperationException("Typed declaration binding reselected a completed MethodImpl");
            Console.WriteLine("[typed declaration binding] self-test OK (exact override structure, generic/inherited slots, owning signatures, presence and local exclusions)");
        }
        finally { _refs = priorReferences; _localTypes = priorLocalTypes; }
    }
}
