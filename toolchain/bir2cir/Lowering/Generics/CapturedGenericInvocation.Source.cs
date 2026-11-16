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

        internal PreparedInvocation(Dictionary<string, List<MethodBinding>> declarations,
            Dictionary<string, ContainerBinding> definitions, bool flatten,
            Func<BirGraphValue, IReadOnlyDictionary<string, TypeNode>, TypeNode> physicalType)
        { this.declarations = declarations; this.definitions = definitions; flattenMissingOwners = flatten; this.physicalType = physicalType; }

        internal void Apply(BirFileDeclaration file, ReferenceMetadataIndex refs, HashSet<string> emittedLocalTypes)
        {
            var generated = new List<IBirTypeDeclaration>();
            BirGraphValue root = file;
            Walk(new BirGraphSlot(() => root, replacement => root = replacement), [], [], file.FileClass.OrDefault(), new());
            if (!ReferenceEquals(root, file)) throw new InvalidOperationException("Captured invocation replaced an owning file");
            if (generated.Count != 0)
            {
                file.Types = file.Types.OrEmpty().Concat(generated).ToArray();
                foreach (var type in generated) emittedLocalTypes.Add(type.Name.Value);
            }

            void Walk(BirGraphSlot slot, BirTypeParameter[] ownerFrame, BirTypeParameter[] methodFrame,
                string lexicalOwner, Dictionary<string, TypeNode> incoming)
            {
                var node = slot.Value;
                if (node == null || node is IBirExecutablePayload) return;
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
                        Walk(child, ownerFrame, methodFrame, lexicalOwner, inner);
                        Declare(child.Value, inner);
                    }
                }
                var capture = TakeCaptureFacts(node);
                if (capture?.Arguments.HasValue != true || !Invocation.Admits(node)) return;
                var call = new Invocation(node);
                if (!call.TypeArguments.HasValue || !call.Arguments.HasValue) return;
                var result = capture.Result ?? BirNodeType.Of(node as BirGraphNode);
                slot.Replace(Expand(call, capture, result, ownerFrame, methodFrame, lexicalOwner, scope, refs, emittedLocalTypes, generated));
            }
        }

        static void Declare(BirGraphValue node, Dictionary<string, TypeNode> scope)
        { if (node is BirVariable { Name.HasValue: true, Type.HasValue: true } variable) scope[variable.Name.Value] = variable.Type.Value; }

        BirGraphValue Expand(Invocation call, CaptureFacts capture, TypeNode result,
            BirTypeParameter[] ownerFrame, BirTypeParameter[] methodFrame, string lexicalOwner,
            Dictionary<string, TypeNode> scope, ReferenceMetadataIndex refs, HashSet<string> emitted, List<IBirTypeDeclaration> generated)
        {
            var captureIndices = capture.Arguments.Value.Select(value => value.Argument.Value).ToArray();
            var captureSlots = capture.Arguments.Value.SelectMany(value => value.Slots.Value).Distinct().ToArray();
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
            var parameterWitnesses = capture.Target != null && _parameterWitnesses.TryGetValue(capture.Target, out var witnesses)
                ? witnesses.Select(type => BirTypeLowering.LowerPhysicalType(type, refs.Aliases, refs.IsValueType, refs.PhysicalTypeNames, typeArg: false)).ToArray()
                : refs.CapturedParameterWitnesses(capture.Target, declarationParameters);
            var witnessFrame = capture.Target != null && _methodWitnessFrames.TryGetValue(capture.Target, out var frame) ? frame : refs.NullableMethodFrame(capture.Target);
            captureSlots = captureSlots.Select(index => witnessFrame?.SourcePosition(index) ?? index).ToArray();
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
            if (declarationParameters.Where((_, index) => !captureIndices.Contains(index)).Any(type => ContainsCapturedSlot(type, captureSlots))) return call.Node;

            if (call.Super)
            {
                // A nonvirtual base call must consume this lexical method's own
                // this. The thunk therefore uses a private instance forwarder,
                // never a base invocation on an arbitrary receiver.
                if (!definitions.TryGetValue(lexicalOwner, out var definition)) throw new InvalidOperationException("Captured super invocation has no lexical owner");
                TypeNode CloseSuper(TypeNode type) => Map(type, variable => variable.Scope == "type"
                    ? owner.Args?[variable.I] ?? throw new InvalidOperationException("Captured super invocation has no base frame") : variable);
                var superFrame = selectedFrame.Select(parameter => CloneParameter(parameter, CloseSuper)).ToArray();
                var superParameters = declarationParameters.Select(CloseSuper).ToArray();
                parameterWitnesses = parameterWitnesses.Select(CloseSuper).ToArray();
                var superReturn = CloseSuper(declaredReturn);
                var forward = call.SuperForward(superParameters, superReturn, selectedFrame.Length, local);
                string superName;
                do { superName = "dotkt$capturedSuper$" + _next++; }
                while (definition.Type.Methods.OrEmpty().Any(method => method.Name.OrDefault() == superName));
                definition.Type.Methods = definition.Type.Methods.OrEmpty().Append(Method(superName, superFrame, superParameters, superReturn,
                    Body(forward, IsVoid(superReturn), objectResult: false), isStatic: false, visibility: "private")).ToArray();
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
            var witnessArguments = new List<int>(); var witnessOwners = new List<TypeNode>();
            var witnessPositions = new List<int>(); var witnessSlots = new List<int>();
            foreach (var argument in captureIndices)
            {
                if (argument >= parameterWitnesses.Length || SignatureValueTypes.Of(CloseDeclaration(parameterWitnesses[argument])) is not TypeNode.Fqn { Args: { } parts } witness)
                    throw new InvalidOperationException("Captured argument has no declaration-owned construction witness");
                for (var position = 0; position < parts.Length; position++)
                    if (parts[position] is TypeNode.Tv { Scope: "method" } variable && captureSlots.Contains(variable.I))
                    {
                        witnessArguments.Add(argument); witnessOwners.Add(new TypeNode.Fqn(definitions.ContainsKey(witness.Name) ? witness.Name : PhysicalName(witness, refs)));
                        witnessPositions.Add(position); witnessSlots.Add(variable.I);
                    }
            }
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
                NewArray(Int, witnessArguments.Select(index => (IBirExpression)Number(index + (call.Instance ? 1 : 0)))), NewArray(Type, witnessOwners.Select(type => (IBirExpression)ClassRef(type))),
                NewArray(Int, witnessPositions.Select(index => (IBirExpression)Number(index))), NewArray(Int, witnessSlots.Select(index => (IBirExpression)Number(index))), NewArray(Object, values) };
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
                new TypeNode.Fqn("kotlin.IntArray"), new TypeNode.Array(new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"))) };
            if (!refs.TryResolveStaticMemberSignature(RuntimeOwner, name, 0, true, signature, null, out _, out var declaration, out var declaringType))
                throw new InvalidOperationException("Captured invocation runtime helper has no exact declaration");
            var member = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(refs, declaration, "method", declaringType, null));
            return new BirNativeStatic { Type = new TypeNode.Fqn(RuntimeOwner), Method = name, Args = arguments, Ret = result,
                MemberRef = member, ResolvedMemberReturn = BirGraphValue.CopyType(member.ReturnType.Value) };
        }

        string RuntimeKey(TypeNode type, ReferenceMetadataIndex refs) => BirTypeLowering.CanonicalPhysicalSlotType(type) switch {
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
