using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExternalGenericConstraintValidation
{
    internal static void SelfTestResolved()
    {
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var prepared = new Prepared(refs, refs.IsValueType, new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, LocalTypeFacts>(StringComparer.Ordinal));
        var integer = new TypeNode.Fqn("System.Int32");
        var text = new TypeNode.Fqn("System.String");
        var enumeration = new TypeNode.Fqn("System.DayOfWeek");
        var objectType = new TypeNode.Fqn("System.Object");
        BirTypeParameter Parameter(string name, params string[] flags) => new() {
            Name = name, SpecialConstraints = flags, Constraints = Array.Empty<TypeNode>(),
        };
        var declarations = new[] { Parameter("T", "struct") };
        var arguments = new TypeNode[] { integer };
        var reference = new BirMemberReference {
            Kind = "method", Assembly = "Probe", DeclaringType = new TypeNode.Fqn("Probe.Owner"), Name = "Selected",
            GenericArity = 1, ParameterTypes = Array.Empty<TypeNode>(), ReturnType = integer,
        };
        // Every current producer of the selected-MethodDef carrier must consume
        // that carrier, without replacing its owning declaration or call inputs.
        var calls = new BirGraphValue[] {
            new BirCallStatic { TypeArgs = arguments, MemberRef = reference, _resolvedMethodTypeParams = declarations },
            new BirCallInstance { TypeArgs = arguments, MemberRef = reference, _resolvedMethodTypeParams = declarations },
            new BirNewDelegate { TypeArgs = arguments, MemberRef = reference, _resolvedMethodTypeParams = declarations },
            new BirNewBoundDelegate { TypeArgs = arguments, MemberRef = reference, _resolvedMethodTypeParams = declarations },
            new BirBoundClrDelegate { TypeArgs = arguments, MemberRef = reference, _resolvedMethodTypeParams = declarations },
            new BirClrStaticDelegate { TypeArgs = arguments, MemberRef = reference, _resolvedMethodTypeParams = declarations },
            new BirConstrainedCall { TypeArgs = arguments, MemberRef = reference, _resolvedMethodTypeParams = declarations },
            new BirGenericStatic { TypeArgs = arguments, MemberRef = reference, _resolvedMethodTypeParams = declarations },
            new BirGenericInstance { TypeArgs = arguments, MemberRef = reference, _resolvedMethodTypeParams = declarations },
            new BirNativeStatic { TypeArgs = arguments, MemberRef = reference, _resolvedMethodTypeParams = declarations },
            new BirNativeInstance { TypeArgs = arguments, MemberRef = reference, _resolvedMethodTypeParams = declarations },
        };
        var uses = calls.Select(ResolvedUseOf).ToArray();
        var statements = calls.Select(call => (IBirStatement)new BirExpressionStatement {
            Expr = OptionalField<IBirExpression>.Of((IBirExpression)call),
        }).ToArray();
        var method = new BirFunctionDeclaration { Name = "run", Params = Array.Empty<BirParameter>(),
            Ret = new TypeNode.Fqn("void"), Body = statements, DeclarationId = "run-declaration" };
        var methods = new[] { method };
        var file = new BirFileDeclaration { FileClass = "ConstraintProbe", Methods = methods };
        prepared.ApplyResolvedMembers(file);
        Assert(ReferenceEquals(file.Methods.Value, methods) && ReferenceEquals(method.Body.Value, statements)
            && method.DeclarationId.Value == "run-declaration", "resolved validation replaced an owning declaration");
        for (var index = 0; index < calls.Length; index++)
            Assert(ResolvedUseOf(calls[index]) == null && ReferenceEquals(uses[index].Arguments.Value, arguments)
                && ReferenceEquals(uses[index].Member, reference)
                && ReferenceEquals(((BirExpressionStatement)statements[index]).Expr.Value, calls[index]),
                "resolved validation lost a selected carrier or changed a call occurrence");

        BirCallStatic Call(TypeNode argument, BirTypeParameter declaration) => new() {
            Method = "displayOnly", TypeArgs = new[] { argument }, MemberRef = reference,
            _resolvedMethodTypeParams = new[] { declaration },
        };
        BirExpressionStatement Statement(BirCallStatic call) => new() { Expr = call };
        void Accept(TypeNode argument, BirTypeParameter declaration) {
            var call = Call(argument, declaration);
            prepared.ApplyResolvedMembers(call);
            Assert(!call._resolvedMethodTypeParams.IsPresent, "a valid selected constraint carrier survived");
        }
        void Reject(TypeNode argument, BirTypeParameter declaration, string requirement) {
            var call = Call(argument, declaration);
            try { prepared.ApplyResolvedMembers(call); }
            catch (InvalidOperationException error) when (error.Message.Contains("CLR generic constraint violation", StringComparison.Ordinal)
                && error.Message.Contains("Probe.Owner.Selected", StringComparison.Ordinal)
                && error.Message.Contains(requirement, StringComparison.Ordinal)) {
                Assert(call._resolvedMethodTypeParams.HasValue, "failing validation consumed the selected constraint fact");
                return;
            }
            throw new InvalidOperationException("Resolved generic validation accepted a violated " + requirement + " constraint");
        }
        Accept(text, Parameter("T", "class"));
        Reject(integer, Parameter("T", "class"), "reference type");
        Accept(integer, Parameter("T", "struct"));
        Reject(new TypeNode.Nullable(integer), Parameter("T", "struct"), "non-nullable CLR value type");
        var enumDeclaration = Parameter("T") with { Constraints = new TypeNode[] { new TypeNode.Fqn("System.Enum") } };
        Accept(enumeration, enumDeclaration);
        Reject(integer, enumDeclaration, "enum type");
        Accept(objectType, Parameter("T", "new"));
        Reject(text, Parameter("T", "new"), "parameterless constructor");

        var captured = Call(new TypeNode.Tv("type", 0), Parameter("T", "class"));
        var own = Call(new TypeNode.Tv("type", 1), Parameter("T", "struct"));
        var genericMethod = Call(new TypeNode.Tv("method", 0), Parameter("T", "new"));
        var constructorCall = Call(new TypeNode.Tv("type", 0), Parameter("T", "class"));
        var ownerMethod = new BirFunctionDeclaration { Name = "generic", TypeParams = new[] { Parameter("M", "class", "new") },
            Body = new IBirStatement[] { Statement(captured), Statement(own), Statement(genericMethod) } };
        var constructor = new BirConstructor { Params = Array.Empty<BirParameter>(),
            Body = new IBirStatement[] { Statement(constructorCall) } };
        var owner = new BirClassDefinition { Name = "Probe.Nested", Kind = "class",
            CapturedTypeParams = new[] { Parameter("Captured", "class") }, TypeParams = new[] { Parameter("Own", "struct") },
            Methods = new[] { ownerMethod }, Ctors = new[] { constructor } };
        var ownerRoots = new BirFileDeclaration { FileClass = "Frames", Types = new IBirTypeDeclaration[] { owner } };
        prepared.ApplyResolvedMembers(ownerRoots);
        Assert(new[] { captured, own, genericMethod, constructorCall }.All(call => !call._resolvedMethodTypeParams.IsPresent)
            && ReferenceEquals(ownerRoots.Types.Value[0], owner) && ReferenceEquals(owner.Methods.Value[0], ownerMethod)
            && ReferenceEquals(owner.Ctors.Value[0], constructor), "resolved validation changed captured/own/method frame ownership");

        // Exact selected declaration types are foreign: do not interpret !N/!!N
        // in these descriptors as arguments in the caller's lexical frame.
        var foreign = reference with { DeclaringType = new TypeNode.Fqn("Probe.Foreign"),
            ParameterTypes = new TypeNode[] { new TypeNode.Nullable(new TypeNode.Tv("method", 0)) } };
        var descriptorCall = Call(integer, Parameter("T", "struct"));
        descriptorCall.MemberRef = foreign;
        descriptorCall.Sig = new TypeNode[] { new TypeNode.Nullable(new TypeNode.Tv("method", 0)) };
        prepared.ApplyResolvedMembers(descriptorCall);
        Assert(ReferenceEquals(descriptorCall.MemberRef.Value, foreign), "resolved validation traversed a foreign descriptor");
        var noCarrier = new BirNativeStatic { TypeArgs = arguments, _resolvedMethodTypeParams = OptionalField<BirTypeParameter[]>.Null };
        prepared.ApplyResolvedMembers(noCarrier);
        Assert(noCarrier._resolvedMethodTypeParams.IsNull, "resolved validation changed explicit-null carrier absence");
        Console.WriteLine("[resolved generic constraints] self-test OK (11 selected carriers, shared flags, captured/method frames, identity, foreign facts)");

        static void Assert(bool condition, string message) {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
