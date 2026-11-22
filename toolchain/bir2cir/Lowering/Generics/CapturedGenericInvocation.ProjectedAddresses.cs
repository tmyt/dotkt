using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CapturedGenericInvocation
{
    static bool RequiresTypedSlot(System.Type type) => type.IsByRef || type.IsByRefLike
        || type.IsGenericParameter && (type.GenericParameterAttributes & GenericParameterAttributes.AllowByRefLike) != 0;

    sealed record AddressTarget(string Name, int Token, string[] Keys, BirTypeParameter[] OwnerFrame,
        BirTypeParameter[] MethodFrame, bool ValueOwner, TypeNode[] Signature, bool[] TypedParameters,
        TypeNode Result, bool TypedResult)
    {
        internal static AddressTarget FromReference(MethodInfo method, BirMemberReference reference)
        {
            var owner = method.DeclaringType;
            if (owner.IsConstructedGenericType) owner = owner.GetGenericTypeDefinition();
            var parameters = method.GetParameters();
            BirTypeParameter[] Frame(System.Type[] frame) => frame.Select(p =>
                ReferenceMetadataReader.GenericParamDeclaration(p, native: true)).ToArray();
            return new(method.Name, method.MetadataToken, parameters.Select(p =>
                ReferenceMetadataIndex.ForeignStarRuntimeTypeKey(p.ParameterType)).ToArray(),
                Frame(owner.GetGenericArguments()), Frame(method.GetGenericArguments()), owner.IsValueType,
                reference.ParameterTypes.Value, parameters.Select(p => RequiresTypedSlot(p.ParameterType)).ToArray(),
                reference.ReturnType.Value, RequiresTypedSlot(method.ReturnType));
        }
    }

    // A verified CLR call cannot use its class bound in place of a missing
    // interface proof. Close a stateless typed forwarder, not the ref/out
    // operands: those keep their original storage across the entire call.
    static BirGraphValue ProjectAddressCall(Invocation site, AddressTarget selected, TypeNode.Fqn owner,
        (int Slot, TypeNode Bound)[] facets, ReferenceMetadataIndex refs, Func<string,string> allocate, List<IBirTypeDeclaration> generated,
        LexicalTarget lexical, BirCallStatic access, TypeNode[] closureArguments, Dictionary<int, TypeNode> fixedAddresses = null,
        CaptureRow[] captures = null, TypeNode capturedResult = null, LiveResultUse resultUse = null)
    {
        var contractName = allocate("addressCall");
        var adapterName = allocate("addressAdapter");
        owner = lexical?.Owner ?? owner;
        var ownerArgs = owner.Args ?? [];
        var authored = site.TypeArguments.Value;
        var ownerParameters = lexical?.OwnerFrame ?? selected.OwnerFrame;
        var methodParameters = lexical?.Method.TypeParams.Value ?? selected.MethodFrame;
        TypeNode AdapterFrame(TypeNode type) => Map(type, t => new TypeNode.Tv("type",
            t.Scope == "type" ? t.I : ownerParameters.Length + t.I));
        TypeNode CallerFrame(TypeNode type) => FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(type, ownerArgs, authored);
        var adapterFrame = ownerParameters.Concat(methodParameters).Select((p, index) => {
            var variable = CloneParameter(p, AdapterFrame);
            variable.Name = "T" + index; variable.Variance = default; return variable;
        }).ToArray();
        TypeNode[] Frame(int start, int count) => Enumerable.Range(start, count).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray();
        var adapterOwner = new TypeNode.Fqn(owner.Name, ownerParameters.Length == 0 ? null : Frame(0, ownerParameters.Length));
        var contractArgs = new List<TypeNode>();
        var implementationArgs = new List<TypeNode>();
        var signature = new List<TypeNode>();
        var implSignature = new List<TypeNode>();
        var forwarded = new List<IBirExpression>();
        var operands = new List<IBirExpression>();
        var witnesses = new List<IBirExpression>();
        var invariantTypes = new List<IBirExpression>();
        var statements = new List<IBirStatement>();
        (TypeNode open, TypeNode impl) BoundarySlot(TypeNode caller, TypeNode impl, bool typed)
        {
            if (!typed) return (Object, Object);
            var parameter = new TypeNode.Tv("type", contractArgs.Count);
            contractArgs.Add(caller is TypeNode.ByRef cr ? cr.Of : caller);
            implementationArgs.Add(impl is TypeNode.ByRef ir ? ir.Of : impl);
            return (impl is TypeNode.ByRef ? new TypeNode.ByRef(parameter) : parameter, impl);
        }
        void Operand(IBirExpression value, TypeNode caller, TypeNode target, bool typed, bool receiver)
        {
            var (parameter, implementation) = BoundarySlot(caller, target, typed);
            signature.Add(parameter);
            implSignature.Add(implementation);
            var name = CallEvalLowering.FreshBindingId();
            var address = caller is TypeNode.ByRef;
            var localType = typed ? caller : Object;
            var initializer = address && value is not BirByRefOf ? (IBirExpression)new BirByRefOf {
                Inner = OptionalField<IBirExpression>.Of(value) } : typed ? value : Cast(Object, value);
            statements.Add(new BirVariable { Name = name, Type = localType, Init = OptionalField<IBirExpression>.Of(initializer) });
            var local = new BirLocal { Name = name, Sty = localType };
            operands.Add(local);
            IBirExpression input = Local(forwarded.Count);
            if (!typed) input = Cast(target, input);
            forwarded.Add(input);
            if (receiver) return;
            witnesses.Add(typed ? Null() : Copy(local));
            invariantTypes.Add(typed ? ClassRef(caller is TypeNode.ByRef r ? r.Of : caller) : Null());
        }
        if (site.Instance)
        {
            bool byAddress = selected.ValueOwner;
            Operand(site.Receiver, byAddress ? new TypeNode.ByRef(owner) : owner,
                byAddress ? new TypeNode.ByRef(adapterOwner) : adapterOwner, byAddress, true);
        }
        var selectedSignature = lexical?.Method.Params.Value.Select(FunctionSignatureIdentity.SignatureType).ToArray() ??
            selected.Signature.Select(SignatureValueTypes.Of).ToArray();
        if (fixedAddresses != null)
        {
            closureArguments = closureArguments.ToArray();
            foreach (var (i, storage) in fixedAddresses)
            {
                var pointee = ((TypeNode.ByRef)SignatureValueTypes.Of(selected.Signature[i])).Of;
                closureArguments[((TypeNode.Tv)pointee).I] = storage;
            }
        }
        for (var i = 0; i < selectedSignature.Length; i++)
        {
            var caller = fixedAddresses?.GetValueOrDefault(i) is { } location
                ? new TypeNode.ByRef(location) : CallerFrame(selectedSignature[i]);
            Operand(site.Arguments.Value[i], caller, AdapterFrame(selectedSignature[i]), selected.TypedParameters[i], false);
        }
        var nativeResult = lexical?.Method.Ret.Value ?? SignatureValueTypes.Of(selected.Result);
        var result = capturedResult ?? CallerFrame(nativeResult);
        var forwardResult = AdapterFrame(nativeResult);
        Func<IBirExpression, IBirExpression[], IBirExpression> consume = null;
        var environment = new List<IBirExpression>();
        bool consumed = false;
        if (selected.TypedResult && resultUse != null && resultUse.TryClose(forwardResult, adapterFrame, refs, out var ordinary, out consume))
        {
            result = ordinary; consumed = true;
            foreach (var (environmentType, value) in resultUse.Environment)
            {
                environment.Add(Local(signature.Count));
                var declared = resultUse.InInvoke(environmentType);
                signature.Add(declared); implSignature.Add(declared); operands.Add(value);
            }
        }
        var unit = IsVoid(consumed ? result : nativeResult);
        var (contractResult, implResult) = consumed ? (resultUse.InInvoke(result), resultUse.InInvoke(result))
            : unit ? (result, result) : BoundarySlot(result, forwardResult, selected.TypedResult);
        TypeNode.Fqn Contract(List<TypeNode> frame) => new(contractName, frame.Count == 0 ? null : frame.ToArray());
        var callerContract = Contract(contractArgs);
        BirGraphValue forward = lexical == null ? site.Forward(adapterOwner, authored.Length, selectedSignature, forwardResult, site.Member == null) :
            site.Instance ? new BirCallInstance { OwnerType = adapterOwner, Method = lexical.Method.Name, Virtual = false,
                Sig = selectedSignature, CalleeRet = nativeResult, Ret = forwardResult } :
            new BirCallStatic { Owner = adapterOwner, Method = lexical.Method.Name, Sig = selectedSignature,
                CalleeRet = nativeResult, Ret = forwardResult };
        var actualArgs = forwarded.Skip(site.Instance ? 1 : 0).ToArray();
        var concreteArgs = Frame(ownerParameters.Length, authored.Length);
        if (access != null)
        {
            // The selected native MethodDef closes the adapter. From within
            // that closed frame, call the original accessibility wrapper with
            // the actual receiver, not the object enclosing the Kotlin call.
            var host = (TypeNode.Fqn)(access.OwnerType.OrDefault() ?? access.Owner.Value);
            forward = new BirCallStatic { Owner = host with { Args = adapterOwner.Args },
                Method = access.Method, Sig = access.Sig, CalleeRet = nativeResult, Ret = forwardResult };
            actualArgs = site.Instance ? forwarded.ToArray() :
                new IBirExpression[] { new BirDefaultValue { Type = adapterOwner } }.Concat(actualArgs).ToArray();
        }
        switch (forward)
        {
            case BirGenericStatic s: s.TypeArgs = concreteArgs; s.Args = actualArgs; break;
            case BirCallStatic s: s.TypeArgs = concreteArgs; s.Args = actualArgs; break;
            case BirCallInstance i: i.TypeArgs = concreteArgs; i.Args = actualArgs; i.Recv = OptionalField<IBirExpression>.Of(forwarded[0]); break;
            case BirGenericInstance i: i.TypeArgs = concreteArgs; i.Args = actualArgs;
                i.Recv = OptionalField<IBirExpression>.Of(selected.ValueOwner ?
                    new BirByRefLoad { Local = "p0", Elem = adapterOwner } : forwarded[0]); break;
        }
        BirFunctionDeclaration Method(List<TypeNode> parameters, TypeNode ret, bool abstractMethod, IBirStatement[] body) => new() {
            Name = "Invoke", Static = false, Vis = "public", Abstract = abstractMethod, Virtual = true, Override = false,
            TypeParams = consumed ? resultUse.InvokeFrame : [], Ret = ret, Body = body, Attrs = Array.Empty<BirAttribute>(),
            Params = parameters.Select((p,i) => new BirParameter { Name = "p"+i, Type = p }).ToArray(),
        };
        generated.Add(new BirInterfaceDefinition {
            Name = contractName, Kind = "interface", Generated = true, Vis = "internal",
            NestedIn = consumed ? resultUse.LexicalOwner : default(OptionalField<string>),
            TypeParams = contractArgs.Select((_, i) => new BirTypeParameter { Name = "P"+i, SpecialConstraints = new[]{"allowsRefStruct"} }).ToArray(),
            Interfaces = Array.Empty<TypeNode>(), Fields = Array.Empty<BirFieldDeclaration>(), Ctors = Array.Empty<BirConstructor>(),
            Methods = new[]{ Method(signature, contractResult, true, []) }, Attrs = Array.Empty<BirAttribute>(),
        });
        var invocation = consumed ? consume((IBirExpression)forward, environment.ToArray()) : (IBirExpression)forward;
        IBirStatement[] body = unit ? [new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(invocation) }, new BirReturn()]
            : [new BirReturn { Value = OptionalField<IBirExpression>.Of(implResult == Object ? Cast(Object, invocation) : invocation) }];
        generated.Add(new BirClassDefinition {
            Name = adapterName, Kind = "class", Generated = true, Vis = "internal", TypeParams = adapterFrame,
            NestedIn = consumed ? resultUse.LexicalOwner : default(OptionalField<string>),
            Interfaces = new TypeNode[]{Contract(implementationArgs)}, Fields = Array.Empty<BirFieldDeclaration>(), Attrs = Array.Empty<BirAttribute>(),
            Ctors = new[]{new BirConstructor { Vis = "public", Params = Array.Empty<BirParameter>(), BaseArgs = OptionalField<IBirExpression[]>.Null,
                Body = Array.Empty<IBirStatement>() }}, Methods = new[]{Method(implSignature, implResult, false, body)},
        });
        const string helperName = "starProjectionIntersectionAdapter";
        var type = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionType");
        var any = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        TypeNode[] lookup = [type, type, new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.String"),
            new TypeNode.Array(new TypeNode.Fqn("kotlin.String")), new TypeNode.Array(type), new TypeNode.Fqn("kotlin.IntArray"),
            new TypeNode.Array(type), new TypeNode.Array(any), new TypeNode.Array(new TypeNode.Nullable(type)),
            new TypeNode.Nullable(new TypeNode.Array(type)), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.IntArray"))];
        if (!refs.TryResolveStaticMemberSignature(RuntimeOwner, helperName, 0, true, lookup, null, out _, out var helper, out var declaring))
            throw new InvalidOperationException($"Projected address call has no runtime binding for '{helperName}'");
        var member = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(null, refs, helper, "method", declaring, null));
        var create = new BirNativeStatic { Type = new TypeNode.Fqn(RuntimeOwner), Method = helperName, MemberRef = member,
            Ret = member.ReturnType, ResolvedMemberReturn = member.ReturnType,
            Args = new IBirExpression[] { ClassRef(new TypeNode.Fqn(adapterName)), ClassRef(owner),
                Number(lexical == null ? selected.Token : 0), Text(lexical?.Method.Name.Value ?? selected.Name),
                NewArray(String, selected.Keys.Select(p => (IBirExpression)Text(p))),
                NewArray(Type, closureArguments.Select(t => (IBirExpression)ClassRef(t))), NewArray(Int, facets.Select(f => (IBirExpression)Number(f.Slot))),
                NewArray(Type, facets.Select(f => (IBirExpression)ClassRef(f.Bound))),
                NewArray(Object, witnesses), NewArray(Type, invariantTypes), CaptureOwners(captures), CaptureCoordinates(captures) } };
        var closedResult = consumed ? result : SupertypeGraph.SubstOwnerTvs(contractResult, contractArgs.ToArray());
        var invoke = new BirCallInstance { OwnerType = callerContract, Method = "Invoke", Virtual = true, Recv = Cast(callerContract, create),
            Sig = signature.ToArray(), Ret = closedResult, CalleeRet = contractResult, Args = operands.ToArray() };
        if (consumed && resultUse.InvokeArguments is { Length: > 0 } frame) invoke.TypeArgs = frame;
        ForeignStarProjectionBinding.RequireRuntimeFallback();
        var block = new BirValueBlock { Type = result, Stmts = statements.ToArray(),
            Result = OptionalField<IBirExpression>.Of(closedResult.Equals(result) ? invoke : Cast(result, invoke)) };
        if (consumed) resultUse.Replacement = block;
        return block;
    }
}
