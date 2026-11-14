using System.Text;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void WellKnownTableSelfTest()
    {
        var prior = _refs;
        try
        {
            var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            var method = new BirFunctionDeclaration {
                Name = "unchanged", Static = true, Params = Array.Empty<BirParameter>(),
                Ret = MemberRefNode.Void, Body = Array.Empty<IBirStatement>(),
            };
            var methods = new[] { method };
            var root = new BirFileDeclaration { FileClass = "WellKnown", Methods = methods };
            ResolveWellKnown(root, references);
            var table = root.WellKnownRefs.Value;
            var roles = new[] {
                "String.ConcatArray", "Type.FromHandle", "Object.GetType", "Object.ToString", "Object.GetHashCode", "Object.Equals",
                "Enum.GetValues", "Enum.Parse", "Object.ctor", "IndexOutOfRangeException.ctor", "NullableT.ctor", "SpanT.ctorPointer",
            };
            if (!table.Members.Keys.SequenceEqual(roles) || !ReferenceEquals(root.Methods.Value, methods)
                || !ReferenceEquals(root.Methods.Value[0], method) || root.Types.IsPresent || root.Fields.IsPresent)
                throw new InvalidOperationException("Typed fixed-member resolution changed role order, completeness or continuing declarations");

            TypeNode Fqn(string name) => new TypeNode.Fqn("System." + name);
            void Check(string role, TypeNode owner, string name, TypeNode result, string convention, params TypeNode[] parameters)
            {
                var reference = table.Members[role];
                if (reference.Assembly != typeof(object).Assembly.GetName().Name
                    || reference.Kind != (role.EndsWith(".ctor", StringComparison.Ordinal) || role == "SpanT.ctorPointer"
                        ? MemberRefNode.Kinds.Ctor : MemberRefNode.Kinds.Method)
                    || !TypeNodeDataEquality.Equal(reference.DeclaringType, owner) || reference.Name != name
                    || reference.GenericArity != 0 || reference.CallingConvention != convention
                    || !TypeNodeDataEquality.Equal(reference.ReturnType, result) || reference.ParameterTypes.Length != parameters.Length
                    || reference.ParameterTypes.Where((value, index) => !TypeNodeDataEquality.Equal(value, parameters[index])).Any())
                    throw new InvalidOperationException("Fixed-member selection changed exact identity/signature for " + role);
            }
            Check("String.ConcatArray", Fqn("String"), "Concat", Fqn("String"), MemberRefNode.Static, new TypeNode.Array(Fqn("Object")));
            Check("Type.FromHandle", Fqn("Type"), "GetTypeFromHandle", Fqn("Type"), MemberRefNode.Static, Fqn("RuntimeTypeHandle"));
            Check("Object.GetType", Fqn("Object"), "GetType", Fqn("Type"), MemberRefNode.Instance);
            Check("Object.ToString", Fqn("Object"), "ToString", Fqn("String"), MemberRefNode.Instance);
            Check("Object.GetHashCode", Fqn("Object"), "GetHashCode", Fqn("Int32"), MemberRefNode.Instance);
            Check("Object.Equals", Fqn("Object"), "Equals", Fqn("Boolean"), MemberRefNode.Instance, Fqn("Object"));
            Check("Enum.GetValues", Fqn("Enum"), "GetValues", Fqn("Array"), MemberRefNode.Static, Fqn("Type"));
            Check("Enum.Parse", Fqn("Enum"), "Parse", Fqn("Object"), MemberRefNode.Static, Fqn("Type"), Fqn("String"));
            Check("Object.ctor", Fqn("Object"), MemberRefNode.CtorName, MemberRefNode.Void, MemberRefNode.Instance);
            Check("IndexOutOfRangeException.ctor", Fqn("IndexOutOfRangeException"), MemberRefNode.CtorName,
                MemberRefNode.Void, MemberRefNode.Instance, Fqn("String"));
            var variable = new TypeNode.Tv("type", 0);
            Check("NullableT.ctor", new TypeNode.Fqn("System.Nullable`1", new TypeNode[] { variable }), MemberRefNode.CtorName,
                MemberRefNode.Void, MemberRefNode.Instance, variable);
            Check("SpanT.ctorPointer", new TypeNode.Fqn("System.Span`1", new TypeNode[] { variable }), MemberRefNode.CtorName,
                MemberRefNode.Void, MemberRefNode.Instance, new TypeNode.Ptr(Fqn("Void")), Fqn("Int32"));

            var sourceMembers = table.Members.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var isolated = PhysicalMemberTable.Create(sourceMembers);
            sourceMembers.Clear();
            if (!table.StructurallyEquals(isolated))
                throw new InvalidOperationException("A physical role table retained its builder's mutable dictionary");

            byte[] Json(BirFileDeclaration file) => BirCarrier.EncodeBody(BirCarrier.JsonV1, file, (writer, value) => value.WriteJson(writer));
            var json = BirGraphJson.ReadFile(Json(root));
            var binary = BirFileCodec.Decode(BirFileCodec.Encode(root));
            if (!root.StructurallyEquals(json) || !root.StructurallyEquals(binary)
                || !json.WellKnownRefs.Value.Members.Keys.SequenceEqual(roles) || !binary.WellKnownRefs.Value.Members.Keys.SequenceEqual(roles))
                throw new InvalidOperationException("Owning role-table codecs changed identity, signatures, presence or role ordering");
            var physical = new TypedCirProjection(new HashSet<string>(StringComparer.Ordinal)).ProjectCirFileDeclaration(root);
            var physicalJson = DeclarationJson.Read(Encoding.UTF8.GetBytes(DeclarationJson.Write(physical)));
            var physicalBinary = CirFileCodec.Decode(CirFileCodec.Encode(physical));
            foreach (var restored in new[] { physicalJson, physicalBinary })
                if (!restored.WellKnownRefs.Value.Keys.SequenceEqual(roles)
                    || !table.StructurallyEquals(PhysicalMemberTable.Create(restored.WellKnownRefs.Value)))
                    throw new InvalidOperationException("Final CIR role-table codecs changed selected physical facts");
            Console.WriteLine("[typed fixed-member table] self-test OK (actual references, twelve exact roles, open signatures, continuing owners, BIR/CIR JSON+MessagePack)");
        }
        finally { _refs = prior; }
    }
}
