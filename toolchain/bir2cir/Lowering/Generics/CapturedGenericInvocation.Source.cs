using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CapturedGenericInvocation
{
    internal sealed class PreparedInvocation
    {
        readonly Dictionary<string, List<MethodBinding>> declarations;
        readonly Dictionary<string, ContainerBinding> definitions;
        readonly Dictionary<string, string> metadataNames = new(StringComparer.Ordinal);
        readonly bool flattenMissingOwners;
        readonly Func<BirGraphValue, IReadOnlyDictionary<string, TypeNode>, TypeNode> physicalType;
        internal bool HasDeferredFixedCalls { get; private set; }
        readonly Func<TypeNode, TypeNode.Fqn> captureConstruction;

        internal PreparedInvocation(Dictionary<string, List<MethodBinding>> declarations,
            Dictionary<string, ContainerBinding> definitions, bool flatten,
            Func<BirGraphValue, IReadOnlyDictionary<string, TypeNode>, TypeNode> physicalType, Func<TypeNode, TypeNode.Fqn> captureConstruction)
        { this.declarations = declarations; this.definitions = definitions; flattenMissingOwners = flatten; this.physicalType = physicalType;
            this.captureConstruction = captureConstruction; }

        internal void Apply(BirFileDeclaration file, ReferenceMetadataIndex refs, HashSet<string> emittedLocalTypes,
            bool finishFixedCalls = false)
        {
            var generated = new List<IBirTypeDeclaration>();
            var liveResults = new Dictionary<BirGraphValue, LiveResultUse>(ReferenceEqualityComparer.Instance);
            BirGraphValue root = file;
            Walk(new BirGraphSlot(() => root, replacement => root = replacement), [], [], file.FileClass.OrDefault(), new());
            if (!ReferenceEquals(root, file)) throw new InvalidOperationException("Captured invocation replaced an owning file");
            if (generated.Count != 0)
            {
                file.Types = file.Types.OrEmpty().Concat(generated).ToArray();
                foreach (var type in generated) emittedLocalTypes.Add(type.Name.Value);
            }

            void Walk(BirGraphSlot slot, BirTypeParameter[] ownerFrame, BirTypeParameter[] methodFrame,
                string lexicalOwner, Dictionary<string, TypeNode> incoming, NativeResultUse resultUse = null)
            {
                var node = slot.Value;
                if (node == null || node is IBirExecutablePayload) return;
                if (liveResults.TryGetValue(node, out var liveUse)) resultUse = liveUse;
                var consumer = NativeResultUse.Direct(node);
                if (node is IBirTypeDeclaration owner && owner.Kind.OrDefault() is "class" or "interface" or "struct")
                { ownerFrame = TypeParameterFrame.CloneDeclarations(owner); lexicalOwner = owner.Name.OrDefault(); }
                if (node is BirFunctionDeclaration { Params.HasValue: true, Body.HasValue: true } function)
                    methodFrame = function.TypeParams.OrEmpty();
                else if (node is BirConstructor { Params.HasValue: true, Body.HasValue: true }) methodFrame = [];
                var scope = incoming;
                var parameters = node switch {
                    BirFunctionDeclaration method => method.Params.OrEmpty(), BirConstructor ctor => ctor.Params.OrEmpty(),
                    IBirTypeDeclaration type => type.Params.OrEmpty(), _ => Array.Empty<BirParameter>(),
                };
                if (parameters.Length != 0)
                {
                    scope = new(scope, StringComparer.Ordinal);
                    foreach (var parameter in parameters)
                        if (parameter.Name.HasValue && parameter.Type.HasValue) scope[parameter.Name.Value] = parameter.Type.Value;
                }
                LiveResultUse live = null;
                var host = lexicalOwner == file.FileClass.OrDefault() ? null : definitions.GetValueOrDefault(lexicalOwner)?.Type;
                if (node is BirFunctionDeclaration enclosing && ownerFrame.Length == 0 && methodFrame.Length == 0 &&
                    lexicalOwner != null && (host != null || lexicalOwner == file.FileClass.OrDefault()))
                {
                    string AllocateHelper() {
                        var methods = (host != null ? host.Methods : file.Methods).OrEmpty();
                        string name;
                        do name = "dotkt$consumeReference$" + _next++; while (methods.Any(m => m.Name.OrDefault() == name));
                        return name;
                    }
                    live = LiveResultUse.Find(enclosing, new(lexicalOwner), scope, AllocateHelper);
                    if (live != null) liveResults.Add(live.Producer, live);
                }
                if (node is BirValueBlock block)
                {
                    var inner = new Dictionary<string, TypeNode>(scope, StringComparer.Ordinal);
                    var statements = block.Stmts.OrEmpty();
                    for (var index = 0; index < statements.Length; index++)
                    {
                        var position = index;
                        Walk(new BirGraphSlot(() => (BirGraphValue)statements[position], value => statements[position] = (IBirStatement)value),
                            ownerFrame, methodFrame, lexicalOwner, inner);
                        Declare(statements[position] as BirGraphValue, inner);
                    }
                    if (block.Result.HasValue)
                        Walk(new BirGraphSlot(() => (BirGraphValue)block.Result.Value,
                            value => block.Result = OptionalField<IBirExpression>.Of((IBirExpression)value)), ownerFrame, methodFrame, lexicalOwner, inner);
                }
                else
                {
                    // Snapshot slots before a descendant appends a super method.
                    // Each original owning vector has its own sequential scope.
                    var children = new List<(string Field, BirGraphSlot Slot)>();
                    node.VisitOwnChildSlots((field, _, child) => children.Add((field, child)));
                    var scopes = new Dictionary<string, Dictionary<string, TypeNode>>(StringComparer.Ordinal);
                    foreach (var (field, child) in children)
                    {
                        if (!scopes.TryGetValue(field, out var inner)) scopes[field] = inner = new(scope, StringComparer.Ordinal);
                        Walk(child, ownerFrame, methodFrame, lexicalOwner, inner,
                            consumer != null && ReferenceEquals(child.Value, consumer.Producer) ? consumer : null);
                        Declare(child.Value, inner);
                    }
                }
                if (live != null)
                {
                    liveResults.Remove(live.Producer);
                    if (live.Replacement != null)
                    {
                        var helper = live.Finish();
                        if (host != null) host.Methods = host.Methods.OrEmpty().Append(helper).ToArray();
                        else file.Methods = file.Methods.OrEmpty().Append(helper).ToArray();
                    }
                }
                if (consumer?.Replacement is { } consumed) { slot.Replace(consumed); return; }
                if (node.MethodWitnessFacts.HasValue && Invocation.Admits(node) &&
                    FixedIntersection(new Invocation(node), lexicalOwner, ownerFrame, refs, emittedLocalTypes, generated,
                        deferVariableArguments: !finishFixedCalls, resultUse: resultUse) is { } fixedCall)
                {
                    slot.Replace(fixedCall);
                    return;
                }
                if (finishFixedCalls) return;
                var capture = TakeCaptureFacts(node);
                if (capture?.Arguments.HasValue != true || !Invocation.Admits(node)) return;
                var call = new Invocation(node);
                if (!call.TypeArguments.HasValue || !call.Arguments.HasValue) return;
                var result = capture.Result ?? BirNodeType.Of(node as BirGraphNode);
                slot.Replace(Expand(call, capture, result, ownerFrame, methodFrame, lexicalOwner, scope, refs, emittedLocalTypes, generated));
            }
        }

        internal BirGraphValue FixedIntersection(Invocation call, string lexicalOwner, BirTypeParameter[] ownerFrame, ReferenceMetadataIndex refs,
            HashSet<string> emitted, List<IBirTypeDeclaration> generated, bool deferVariableArguments = false,
            CallableWitnessScope classScope = default, CallableWitnessScope methodScope = default,
            Func<string,string> allocate = null, IBirTypeDeclaration lexicalDefinition = null, NativeResultUse resultUse = null)
        {
            var occurrence = call.Node;
            call = SelectedInvocation(call, out var access);
            var id = call.Node.SelectedGenericCallable;
            var owner = (TypeNode.Fqn)(call.Member?.DeclaringType.OrDefault() ?? call.Owner);
            var local = id != null && declarations.TryGetValue(id, out var bindings)
                ? bindings.SingleOrDefault(b => b.Owner == owner.Name && b.Method.Name.OrDefault() == call.Method)?.Method : null;
            if (!(id == null && call.Member != null) && call.Node is not (BirGenericInstance or BirGenericStatic) &&
                !ProjectedCallableEntries.HasFixedAbi(local, id, refs)) return null;
            // A physical copy may split a second argument that still names a lexical
            // variable here. Keep the selected call until that copy has its own frame.
            if (deferVariableArguments && call.TypeArguments.Value.Any(t => t is TypeNode.Tv))
            {
                HasDeferredFixedCalls = true;
                return occurrence;
            }
            var member = call.Member;
            if (local == null && member == null) throw new InvalidOperationException("Fixed invocation lacks its selected declaration");
            AddressTarget target;
            bool lexicalAccess = false;
            if (member != null)
            {
                if (!TrySelectedMethod(call, owner, refs, out var method))
                    throw new InvalidOperationException("Fixed invocation lost its selected MethodDef");
                target = AddressTarget.FromReference(method, member);
                lexicalAccess = !method.IsPublic;
            }
            else
            {
                var definition = definitions[owner.Name].Type;
                var ownerParameters = TypeParameterFrame.Declarations(definition).ToArray();
                var methodParameters = local.TypeParams.OrEmpty();
                bool Typed(TypeNode type) => SignatureValueTypes.Of(type) is TypeNode.Tv v
                    ? (v.Scope == "method" ? methodParameters : ownerParameters)[v.I].SpecialConstraints.OrEmpty().Contains("allowsRefStruct")
                    : HasAddress(type) || ByRefLike(SignatureValueTypes.Of(type), refs);
                var formals = local.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).ToArray();
                // A local MethodDef has no token yet. Bind its complete physical
                // descriptor. Zero is reserved for unique lexical thunks.
                target = new(local.Name.Value, -1, formals.Select(t => RuntimeKey(SignatureValueTypes.Of(t), refs)).ToArray(),
                    ownerParameters, methodParameters, definition.Kind.Value == "struct", formals,
                    formals.Select(Typed).ToArray(), local.Ret.Value, Typed(local.Ret.Value));
            }
            var signature = target.Signature;
            var ret = target.Result;
            var actual = call.TypeArguments.Value;
            var authored = actual.ToArray();
            var proofs = occurrence.MethodWitnessFacts.Value;
            foreach (var proof in proofs) authored[proof.Argument.Value] = proof.Primary.Value;
            var lexicalSlots = LostLexicalSlots(Enumerable.Range(0, actual.Length)
                .Where(i => CallableWitnessScope.For(actual[i], classScope, methodScope).Any()).ToArray(),
                proofs.Select(p => p.Argument.Value),
                i => target.MethodFrame[i].Constraints.OrEmpty().OfType<TypeNode.Fqn>().Any(t =>
                    definitions.TryGetValue(t.Name, out var declaration) ? declaration.Type.Kind.OrDefault() == "interface" : refs.IsInterfaceType(t)),
                (i, lost) => target.MethodFrame[i].Constraints.OrEmpty().Any(t => ContainsCapturedSlot(t, lost)));
            var facets = proofs.SelectMany(p => p.Interfaces.Value.Select(bound => (p.Argument.Value, bound)))
                .Concat(lexicalSlots.SelectMany(index => CallableWitnessScope.For(actual[index], classScope, methodScope).SelectMany(proof =>
                        new (int, TypeNode)[] { (index, proof.Slot.Bound), (index, proof.Variable) })))
                .Distinct().ToArray();
            var unit = IsVoid(ret);
            var typed = target.ValueOwner && call.Instance || target.TypedParameters.Contains(true) || target.TypedResult;
            LexicalTarget lexical = null;
            var capture = TakeCaptureFacts(occurrence);
            var rows = capture?.Arguments.HasValue == true ? CaptureRows(capture, signature, refs) : [];
            // IKey<capture> is not IKey<object>. Keep the projected result
            // that belongs to this expression, not the fallback MethodSpec.
            var capturedResult = rows.Length == 0 ? null : capture.Result ?? BirNodeType.Of(occurrence as BirGraphNode);
            var result = capturedResult ?? FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(ret, owner.Args ?? [], actual);
            occurrence.MethodWitnessFacts = default;
            if (access == null && (call.Super || typed && lexicalAccess))
            {
                var formals = signature.Select(t => SupertypeGraph.SubstOwnerTvs(t, owner.Args ?? [])).ToArray();
                var returns = SupertypeGraph.SubstOwnerTvs(ret, owner.Args ?? []);
                var thunk = LexicalForwarder(call, owner, lexicalOwner, target.MethodFrame.Select(p => CloneParameter(p,
                    t => SupertypeGraph.SubstOwnerTvs(t, owner.Args ?? []))).ToArray(), formals, returns, local != null, typed, lexicalDefinition);
                lexical = new(new(lexicalOwner, ownerFrame.Length == 0 ? null : Enumerable.Range(0, ownerFrame.Length)
                    .Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray()), ownerFrame, thunk);
            }
            string Allocate(string role) {
                string name;
                do name = lexicalOwner + "$" + role + _next++; while (emitted.Contains(name));
                return name;
            }
            return typed || access != null ? ProjectAddressCall(call, target, owner, facets, refs, allocate ?? Allocate, generated, lexical, access, authored,
                captures: rows, capturedResult: capturedResult, resultUse: resultUse)
                : IntersectionCall(call, lexical?.Owner ?? owner, lexical == null ? target.Token : 0,
                    lexical?.Method.Name.Value ?? target.Name, target.Keys, authored, facets, result, unit, refs, rows);
        }

        BirFunctionDeclaration LexicalForwarder(Invocation call, TypeNode.Fqn target, string lexicalOwner, BirTypeParameter[] frame,
            TypeNode[] parameters, TypeNode result, bool local, bool adapterAccess = false, IBirTypeDeclaration host = null)
        {
            var definition = host ?? definitions[lexicalOwner].Type;
            string name;
            do { name = "dotkt$capturedSuper$" + _next++; }
            while (definition.Methods.OrEmpty().Any(method => method.Name.OrDefault() == name));
            var forward = call.Instance ? call.SuperForward(parameters, result, frame.Length, local)
                : call.Forward(target, frame.Length, parameters, result, local);
            var method = Method(name, frame, parameters, result,
                Body(forward, IsVoid(result), objectResult: false), isStatic: !call.Instance, visibility: adapterAccess ? "internal" : "private");
            definition.Methods = definition.Methods.OrEmpty().Append(method).ToArray();
            return method;
        }

        static void Declare(BirGraphValue node, Dictionary<string, TypeNode> scope)
        { if (node is BirVariable { Name.HasValue: true, Type.HasValue: true } variable) scope[variable.Name.Value] = variable.Type.Value; }

        CaptureRow[] CaptureRows(CaptureFacts capture, TypeNode[] declarationParameters, ReferenceMetadataIndex refs)
        {
            var witnesses = capture.Target != null && _parameterWitnesses.TryGetValue(capture.Target, out var source)
                ? source.Select(t => BirTypeLowering.LowerPhysicalType(captureConstruction?.Invoke(t) ?? t,
                    refs.Aliases, refs.IsValueType, refs.PhysicalTypeNames, typeArg: false)).ToArray()
                : refs.CapturedParameterWitnesses(capture.Target, declarationParameters, captureConstruction);
            var frame = capture.Target != null && _methodWitnessFrames.TryGetValue(capture.Target, out var localFrame)
                ? localFrame : refs.NullableMethodFrame(capture.Target);
            var rows = new List<CaptureRow>();
            foreach (var entry in capture.Arguments.Value)
            {
                var index = entry.Argument.Value;
                if (CaptureOwner(SignatureValueTypes.Of(witnesses[index])) is not { Args: { } parts } owner)
                    throw new InvalidOperationException("Captured argument has no declaration-owned construction witness");
                var head = new TypeNode.Fqn(definitions.ContainsKey(owner.Name) ? owner.Name : PhysicalName(owner, refs));
                foreach (var logical in entry.Slots.Value)
                {
                    var slot = frame?.SourcePosition(logical) ?? logical;
                    var nullable = frame?.NullableIndices.Contains(logical) == true ? frame.NullableVariable(new TypeNode.Tv("method", logical)).I : -1;
                    for (int pos = 0; pos < parts.Length; pos++)
                        if (parts[pos] is TypeNode.Tv { Scope: "method" } t && t.I == slot)
                            rows.Add(new(index, head, pos, slot, nullable));
                }
            }
            return rows.ToArray();
        }

        BirGraphValue Expand(Invocation call, CaptureFacts capture, TypeNode result,
            BirTypeParameter[] ownerFrame, BirTypeParameter[] methodFrame, string lexicalOwner,
            Dictionary<string, TypeNode> scope, ReferenceMetadataIndex refs, HashSet<string> emitted, List<IBirTypeDeclaration> generated)
        {
            var captureIndices = capture.Arguments.Value.Select(value => value.Argument.Value).ToArray();
            var arguments = call.Arguments.Value;
            var selectedFrame = call.SelectedFrame;
            var local = call.Member == null;
            TypeNode.Fqn owner;
            TypeNode[] declarationParameters;
            TypeNode declaredReturn;
            if (local)
            {
                if (capture.Target == null || call.Owner is not TypeNode.Fqn localOwner
                    || !declarations.TryGetValue(capture.Target, out var matches)) return call.Node;
                var exact = matches.Where(binding => binding.Owner == localOwner.Name && binding.Method.Name.OrDefault() == call.Method).ToArray();
                if (exact.Length != 1) throw new InvalidOperationException("Captured local invocation has no unique declaration identity");
                var declaration = exact[0].Method;
                selectedFrame = declaration.TypeParams.OrEmpty(); owner = localOwner;
                declarationParameters = declaration.Params.Value.Select(parameter => SignatureValueTypes.Of(parameter.Type.Value)).ToArray();
                declaredReturn = SignatureValueTypes.Of(declaration.Ret.Value);
            }
            else
            {
                owner = call.Member.DeclaringType.OrDefault() as TypeNode.Fqn
                    ?? throw new InvalidOperationException("Captured invocation has no selected declaring type");
                declarationParameters = call.Member.ParameterTypes.Value.Select(SignatureValueTypes.Of).ToArray();
                declaredReturn = SignatureValueTypes.Of(call.Member.ReturnType.OrDefault());
            }
            if (selectedFrame == null) throw new InvalidOperationException("Captured invocation has no selected generic frame");
            if (captureIndices.Any(index => index < 0 || index >= arguments.Length) || declarationParameters.Length != arguments.Length)
                throw new InvalidOperationException("Captured invocation argument does not match selected declaration");
            // This value transport does not change managed-address storage.
            // Leave other transports on their existing physical path; a compiler
            // limitation here must not introduce a new refusal.
            if (declarationParameters.Any(HasAddress) || HasAddress(declaredReturn)
                || declarationParameters.Any(type => ByRefLike(type, refs)) || ByRefLike(declaredReturn, refs)) return call.Node;
            // Closing the method cannot convert an already-created invariant
            // construction into the captured construction. Do not replace that
            // existing edge with a verifier-green but uncallable thunk.
            var rows = CaptureRows(capture, declarationParameters, refs);
            var closedSlots = rows.SelectMany(row => new[] { row.Slot, row.Nullable }).Where(i => i >= 0).ToArray();
            if (declarationParameters.Where((_, index) => !captureIndices.Contains(index)).Any(type => ContainsCapturedSlot(type, closedSlots))) return call.Node;

            if (call.Super)
            {
                // A nonvirtual base call must consume this lexical method's own
                // this. The thunk therefore uses a private instance forwarder,
                // never a base invocation on an arbitrary receiver.
                TypeNode CloseSuper(TypeNode type) => Map(type, variable => variable.Scope == "type"
                    ? owner.Args?[variable.I] ?? throw new InvalidOperationException("Captured super invocation has no base frame") : variable);
                var superFrame = selectedFrame.Select(parameter => CloneParameter(parameter, CloseSuper)).ToArray();
                var superParameters = declarationParameters.Select(CloseSuper).ToArray();
                var superReturn = CloseSuper(declaredReturn);
                var superName = LexicalForwarder(call, owner, lexicalOwner, superFrame, superParameters, superReturn, local).Name.Value;
                owner = new TypeNode.Fqn(lexicalOwner, ownerFrame.Length == 0 ? null
                    : Enumerable.Range(0, ownerFrame.Length).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
                declarationParameters = superParameters; declaredReturn = superReturn; selectedFrame = superFrame; local = true;
                var replacement = call.Node is BirCallInstance ordinary ? (BirCallInstance)ordinary.DeepClone() : new BirCallInstance {
                    Recv = OptionalField<IBirExpression>.Of(Copy(call.Receiver)), Args = call.Arguments, TypeArgs = call.TypeArguments,
                };
                replacement.Method = superName; replacement.OwnerType = owner; replacement.Sig = superParameters;
                replacement.Super = false; replacement.Virtual = false; replacement.MemberRef = default;
                replacement.ResolvedMemberParams = default; replacement.ResolvedMemberReturn = default;
                call = new Invocation(replacement);
            }

            var arity = selectedFrame.Length;
            TypeNode LiftCaller(TypeNode type) => Map(type, variable => new TypeNode.Tv("method", arity + (variable.Scope == "type" ? variable.I : ownerFrame.Length + variable.I)));
            var liftedOwner = (TypeNode.Fqn)LiftCaller(owner);
            TypeNode CloseDeclaration(TypeNode type) => Map(type, variable => variable.Scope == "type"
                ? liftedOwner.Args?[variable.I] ?? throw new InvalidOperationException("Captured invocation has no owner frame") : variable);
            var fullFrame = selectedFrame.Select(parameter => CloneParameter(parameter, CloseDeclaration))
                .Concat(ownerFrame.Concat(methodFrame).Select(parameter => CloneParameter(parameter, LiftCaller))).ToArray();
            for (var index = 0; index < fullFrame.Length; index++) { fullFrame[index].Name = "__captured" + index; fullFrame[index].Variance = default; }
            var parameterTypes = declarationParameters.Select(CloseDeclaration).ToList();
            if (call.Instance)
            {
                // The declaring owner and receiver storage need not be the same
                // type. Retaining the actual receiver also preserves lexical
                // family access for a thunk nested in its caller.
                var receiverType = call.Receiver is BirThis ? new TypeNode.Fqn(lexicalOwner, ownerFrame.Length == 0 ? null
                    : Enumerable.Range(0, ownerFrame.Length).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray())
                    : physicalType((BirGraphValue)call.Receiver, scope);
                if (receiverType == null) throw new InvalidOperationException("Captured invocation has no physical receiver type");
                parameterTypes.Insert(0, LiftCaller(receiverType));
            }
            var forwardCall = call.Forward(liftedOwner, arity, declarationParameters, CloseDeclaration(declaredReturn), local);
            string name;
            do { name = lexicalOwner + ".dotkt$captured$" + _next++; } while (emitted.Contains(name));
            var returnsVoid = IsVoid(declaredReturn);
            var body = Body(forwardCall, returnsVoid, objectResult: true);
            var receiverPlan = call.Instance && !local && refs.IsValueType(owner)
                ? CapturedValueReceiverInvocation.Create(name, lexicalOwner, ownerFrame, methodFrame, fullFrame, owner, liftedOwner, forwardCall, returnsVoid) : null;
            // Receiver-plan Invoke is a frame-closing factory; the live managed
            // address travels separately and must never enter object[].
            if (receiverPlan != null)
            { parameterTypes[0] = Object; body = receiverPlan.FactoryBody; generated.Add(receiverPlan.Contract); generated.Add(receiverPlan.Adapter); }
            // Nesting retains caller access, not an additional TypeDef frame.
            generated.Add(new BirClassDefinition { Name = name, Kind = "class", Generated = true, Vis = "internal", NestedIn = lexicalOwner,
                TypeParams = Array.Empty<BirTypeParameter>(), Interfaces = Array.Empty<TypeNode>(), Fields = Array.Empty<BirFieldDeclaration>(),
                Ctors = Array.Empty<BirConstructor>(), Attrs = Array.Empty<BirAttribute>(), Methods = new[] {
                    Method("Invoke", fullFrame, parameterTypes.ToArray(), returnsVoid && receiverPlan == null ? new TypeNode.Fqn("void") : Object, body, true, "public") } });
            var values = arguments.Select(argument => (IBirExpression)Cast(Object, Copy(argument))).ToList();
            if (call.Instance) values.Insert(0, receiverPlan == null ? Cast(Object, Copy(call.Receiver)) : Null());
            var fallback = call.TypeArguments.Value.Concat(Enumerable.Range(0, ownerFrame.Length).Select(index => (TypeNode)new TypeNode.Tv("type", index)))
                .Concat(Enumerable.Range(0, methodFrame.Length).Select(index => (TypeNode)new TypeNode.Tv("method", index))).ToArray();
            var runtimeMethod = returnsVoid && receiverPlan == null ? "starProjectionInvokeCapturedHelperUnit" : "starProjectionInvokeCapturedHelper";
            var runtimeArguments = new IBirExpression[] { ClassRef(new TypeNode.Fqn(name)), Number(0), Text("Invoke"),
                NewArray(String, parameterTypes.Select(type => (IBirExpression)Text(RuntimeKey(type, refs)))), NewArray(Type, fallback.Select(type => (IBirExpression)ClassRef(type))),
                NewArray(Int, rows.Select(r => Number(r.Argument + (call.Instance ? 1 : 0)))), NewArray(Type, rows.Select(r => ClassRef(r.Owner))),
                NewArray(Int, rows.Select(r => Number(r.Position))), NewArray(Int, rows.Select(r => Number(r.Slot))),
                NewArray(Int, rows.Select(r => Number(r.Nullable))), NewArray(Object, values) };
            var runtimeCall = RuntimeCall(runtimeMethod, runtimeArguments, returnsVoid && receiverPlan == null, refs);
            ForeignStarProjectionBinding.RequireRuntimeFallback();
            var invocation = receiverPlan == null ? runtimeCall : CapturedValueReceiverInvocation.Invoke(receiverPlan, call.Receiver, runtimeCall, name, RuntimeSignature.Length - 1);
            return returnsVoid || result == null || IsObject(result) ? (BirGraphValue)invocation : Cast(result, invocation);
        }

        IBirExpression RuntimeCall(string name, IBirExpression[] arguments, bool unit, ReferenceMetadataIndex refs)
        {
            var local = declarations.Values.SelectMany(bindings => bindings).Where(binding => binding.Owner == RuntimeOwner
                && binding.Method.Name.OrDefault() == name && binding.Method.TypeParams.OrEmpty().Length == 0
                && binding.Method.Params.HasValue && binding.Method.Params.Value.Length == RuntimeSignature.Length
                && binding.Method.Params.Value.Select(parameter => BirTypeLowering.CanonicalPhysicalSlotType(parameter.Type.OrDefault()))
                    .SequenceEqual(RuntimeSignature.Select(BirTypeLowering.CanonicalPhysicalSlotType))).ToArray();
            if (local.Length > 1) throw new InvalidOperationException("Captured invocation runtime helper has ambiguous local declarations");
            var result = unit ? new TypeNode.Fqn("void") : Object;
            if (local.Length == 1) return new BirCallStatic { Owner = new TypeNode.Fqn(RuntimeOwner), Method = name, Args = arguments, Ret = result,
                Sig = local[0].Method.Params.Value.Select(parameter => BirGraphValue.CopyType(parameter.Type.Value)).ToArray(), CalleeRet = BirGraphValue.CopyType(local[0].Method.Ret.Value) };
            // Select through the trusted reference twin's alias vocabulary;
            // the runtime twin's physical storage is not a new overload policy.
            var runtimeType = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionType");
            var signature = new TypeNode[] { runtimeType, new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.String"), new TypeNode.Array(new TypeNode.Fqn("kotlin.String")),
                new TypeNode.Array(runtimeType), new TypeNode.Fqn("kotlin.IntArray"), new TypeNode.Array(runtimeType), new TypeNode.Fqn("kotlin.IntArray"),
                new TypeNode.Fqn("kotlin.IntArray"), new TypeNode.Fqn("kotlin.IntArray"), new TypeNode.Array(new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"))) };
            if (!refs.TryResolveStaticMemberSignature(RuntimeOwner, name, 0, true, signature, null, out _, out var declaration, out var declaringType))
                throw new InvalidOperationException("Captured invocation runtime helper has no exact declaration");
            var member = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(null, refs, declaration, "method", declaringType, null));
            return new BirNativeStatic { Type = new TypeNode.Fqn(RuntimeOwner), Method = name, Args = arguments, Ret = result,
                MemberRef = member, ResolvedMemberReturn = BirGraphValue.CopyType(member.ReturnType.Value) };
        }

        string RuntimeKey(TypeNode type, ReferenceMetadataIndex refs) => BirTypeLowering.CanonicalPhysicalSlotType(type) switch {
            TypeNode.ByRef reference => "r[" + RuntimeKey(reference.Of, refs) + "]",
            TypeNode.Tv variable => (variable.Scope == "method" ? "m" : "t") + variable.I,
            TypeNode.Nullable nullable => nullable.Of is TypeNode.Fqn named && (definitions.TryGetValue(named.Name, out var local)
                ? local.Type.Kind.OrDefault() is "struct" or "enum" : refs.IsValueType(named)) ? "g{System.Nullable`1}<" + RuntimeKey(nullable.Of, refs) + ">" : RuntimeKey(nullable.Of, refs),
            TypeNode.Array array => "a" + (array.SzArray ? "s" : "m") + array.Rank + "[" + RuntimeKey(array.Elem, refs) + "]",
            TypeNode.Fn function => RuntimeKey(BirTypeLowering.DelegateFqnOf(function) ?? throw new InvalidOperationException("Captured invocation function parameter has no physical delegate"), refs),
            TypeNode.Fqn named => (named.Args is { Length: > 0 } ? "g{" : "n{") + PhysicalName(named, refs) + "}"
                + (named.Args is { Length: > 0 } args ? "<" + string.Join(",", args.Select(argument => RuntimeKey(argument, refs))) + ">" : ""),
            _ => throw new InvalidOperationException("Captured invocation has an unsupported structural parameter type"),
        };
        string PhysicalName(TypeNode.Fqn type, ReferenceMetadataIndex refs) => type.Name switch {
            "object" => "System.Object", "int" => "System.Int32", "string" => "System.String",
            _ => definitions.ContainsKey(type.Name) ? LocalMetadataName(type.Name)
                : refs.TryExactPhysicalTypeName(type.Name, type.Args?.Length ?? 0, out var name) ? name : type.Name,
        };
        string LocalMetadataName(string name)
        {
            if (metadataNames.TryGetValue(name, out var known)) return known;
            var type = definitions[name].Type;
            var arity = type.TypeParams.OrEmpty().Length;
            var parent = type.NestedIn.OrDefault();
            // Flattening is an explicit build contract, not legacy-name inference.
            if (parent != null && !definitions.ContainsKey(parent) && flattenMissingOwners) parent = null;
            var simple = parent == null ? name : name[(name.LastIndexOf('.') + 1)..];
            var physical = type.Kind.OrDefault() == "delegate" || arity == 0 ? simple : simple + "`" + arity;
            return metadataNames[name] = parent == null ? physical : LocalMetadataName(parent) + "+" + physical;
        }
    }

    static bool IsVoid(TypeNode type) => type is TypeNode.Fqn { Name: "void" or "System.Void" };
    static IBirStatement[] Body(BirGraphValue call, bool unit, bool objectResult) => unit
        ? new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of((IBirExpression)call) } }
        : new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(objectResult ? Cast(Object, (IBirExpression)call) : (IBirExpression)call) } };
    static BirFunctionDeclaration Method(string name, BirTypeParameter[] frame, TypeNode[] parameters, TypeNode result,
        IBirStatement[] body, bool isStatic, string visibility) => new() {
        Name = name, Vis = visibility, Static = isStatic, Override = false, Virtual = false, Abstract = false, ObjectOverride = false,
        TypeParams = frame, Ret = result, Params = parameters.Select((type, index) => new BirParameter { Name = "p" + index, Type = type }).ToArray(),
        Attrs = Array.Empty<BirAttribute>(), Body = body,
    };
}
