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
        readonly Dictionary<BirGraphValue, (CallableWitnessScope Owner, CallableWitnessScope Method, IReadOnlyDictionary<string,TypeNode> Locations)> resultScopes =
            new(ReferenceEqualityComparer.Instance);
        readonly Func<TypeNode, TypeNode.Fqn> captureConstruction;
        readonly ExternalGenericConstraintValidation.Prepared constraintValidation;
        readonly Lazy<Dictionary<string, SupertypeGraph.Def>> classGraph;

        internal PreparedInvocation(Dictionary<string, List<MethodBinding>> declarations,
            Dictionary<string, ContainerBinding> definitions, bool flatten,
            Func<BirGraphValue, IReadOnlyDictionary<string, TypeNode>, TypeNode> physicalType, Func<TypeNode, TypeNode.Fqn> captureConstruction,
            ExternalGenericConstraintValidation.Prepared constraintValidation, Lazy<Dictionary<string, SupertypeGraph.Def>> classGraph)
        { this.declarations = declarations; this.definitions = definitions; flattenMissingOwners = flatten; this.physicalType = physicalType;
            this.captureConstruction = captureConstruction; this.constraintValidation = constraintValidation; this.classGraph = classGraph; }

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
                string lexicalOwner, Dictionary<string, TypeNode> incoming, TypeNode lexicalReturn = null, bool resultFrame = false)
            {
                var node = slot.Value;
                if (node == null || node is IBirExecutablePayload) return;
                liveResults.TryGetValue(node, out var resultUse);
                if (ResultExpression(node, methodFrame, lexicalReturn, refs, finishFixedCalls, LocalConsumer, out var relay) is { } region)
                {
                    BirGraphValue regionRoot = region;
                    Walk(new BirGraphSlot(() => regionRoot, value => regionRoot = value), ownerFrame, methodFrame, lexicalOwner, incoming, resultFrame: true);
                    slot.Replace(ExpressionBody(region, relay));
                    return;
                }
                if (node is IBirTypeDeclaration owner && owner.Kind.OrDefault() is "class" or "interface" or "struct")
                { ownerFrame = TypeParameterFrame.CloneDeclarations(owner); lexicalOwner = owner.Name.OrDefault(); }
                if (node is BirFunctionDeclaration { Params.HasValue: true, Body.HasValue: true } function)
                { methodFrame = function.TypeParams.OrEmpty(); lexicalReturn = function.Ret.OrDefault(); }
                else if (node is BirConstructor { Params.HasValue: true, Body.HasValue: true })
                { methodFrame = []; lexicalReturn = new TypeNode.Fqn("void"); }
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
                if (!resultFrame && node is (BirFunctionDeclaration or BirConstructor or BirBlock or BirBranch or BirValueBlock or BirCatch or BirTry or BirFor or BirForArray or
                    BirForRange or BirForEach or BirRepeat or BirWhile))
                    node.VisitSourceChildren((_, _, _) => {}, nested => {
                        if (nested.ReplaceStatements == null) return;
                        var statements = nested.Statements;
                        // An inline block used as a local initializer belongs
                        // to this same Kotlin frame. Keep its consumer in the
                        // producer's region, rather than exporting the span.
                        List<IBirStatement> flat = null;
                        for (int i = 0; i < statements.Length; i++)
                        {
                            var stmt = statements[i];
                            if (stmt is BirVariable { Init.HasValue: true, Type.HasValue: true } v &&
                                v.Init.Value is BirValueBlock b && ByRefLike(v.Type.Value, refs) &&
                                LiveResultUse.BlockProducer(b, refs, finishFixedCalls) >= 0)
                            {
                                flat ??= new(statements[..i]);
                                flat.AddRange(b.Stmts.Value);
                                v.Init = b.Result;
                            }
                            flat?.Add(stmt);
                        }
                        if (flat != null) nested.ReplaceStatements(statements = flat.ToArray());
                        var first = Array.FindIndex(statements, s => LiveResultUse.ProducerOf(s, refs, finishFixedCalls) != null);
                        if (first < 0) return;
                        int end = LiveResultUse.SingleEntryEnd(statements, first);
                        // Ordinary tail returns can return directly through
                        // the adapter. Only a jump back into the caller needs
                        // the same transfer envelope as a nested statement.
                        if (node is BirFunctionDeclaration && end == statements.Length &&
                            TypedProtectedRegions.Edges(TypedProtectedRegions.Statements(statements[(first+1)..]), externalOnly: true)
                                .All(e => e.Slot.Value is BirReturn or BirReturnExpression)) return;
                        var before = statements[..first];
                        var prefix = new List<IBirStatement> { statements[first] };
                        var rest = statements[(first+1)..end];
                        var parts = TypedProtectedRegions.Statements(rest).AsEnumerable();
                        var output = end == statements.Length ? nested.Result : null;
                        if (output is { } resultSlot) parts = parts.Append(resultSlot);
                        var inner = BodyScope(node, scope);
                        foreach (var s in before) Declare((BirGraphValue)s, inner);
                        var ret = output != null && node is BirValueBlock vb ? vb.Type.OrDefault() ??
                            physicalType(output.Value.Value, inner) : new TypeNode.Fqn("void");
                        var exits = ResultReturn.Extract(parts.ToArray(), lexicalReturn, ret, prefix);
                        prefix.AddRange(rest);
                        var value = output?.Value as IBirExpression ??
                            new BirConstant { Type = ret, Value = new BirLiteral.NullLiteral() };
                        var fn = ResultFrame(prefix, value, ret, methodFrame, exits);
                        BirGraphValue owned = fn;
                        Walk(new BirGraphSlot(() => owned, replacement => owned = replacement), ownerFrame, methodFrame, lexicalOwner, inner, resultFrame: true);
                        var lowered = ExpressionBody(fn, exits);
                        if (output is { } tail)
                        {
                            nested.ReplaceStatements([..before, ..lowered.Stmts.Value]);
                            tail.Replace((BirGraphValue)lowered.Result.Value);
                        }
                        else nested.ReplaceStatements([..before, new BirExpressionStatement { Expr = lowered }, ..statements[end..]]);
                    });
                LiveResultUse live = null;
                IBirTypeDeclaration host = null;
                if (node is BirFunctionDeclaration enclosing && lexicalOwner != null &&
                    (lexicalOwner == file.FileClass.OrDefault() || (host = definitions.GetValueOrDefault(lexicalOwner)?.Type) != null))
                {
                    string AllocateHelper() {
                        var methods = (host != null ? host.Methods : file.Methods).OrEmpty();
                        string name;
                        do name = "dotkt$consumeResult$" + _next++; while (methods.Any(m => m.Name.OrDefault() == name));
                        return name;
                    }
                    live = LiveResultUse.Find(enclosing, lexicalOwner, ownerFrame, host?.Kind.OrDefault() == "struct", refs, scope, AllocateHelper, finishFixedCalls);
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
                            ownerFrame, methodFrame, lexicalOwner, inner, lexicalReturn);
                        Declare(statements[position] as BirGraphValue, inner);
                    }
                    if (block.Result.HasValue)
                        Walk(new BirGraphSlot(() => (BirGraphValue)block.Result.Value,
                            value => block.Result = OptionalField<IBirExpression>.Of((IBirExpression)value)), ownerFrame, methodFrame, lexicalOwner, inner, lexicalReturn);
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
                        // The rest of this body belongs to the lexical continuation.
                        // Its captures must open there, in the extended method frame.
                        if (live?.Replacement != null && field == "body") continue;
                        if (!scopes.TryGetValue(field, out var inner)) scopes[field] = inner = field == "body" ?
                            BodyScope(node, scope) : new(scope, StringComparer.Ordinal);
                        Walk(child, ownerFrame, methodFrame, lexicalOwner, inner, lexicalReturn);
                        Declare(child.Value, inner);
                    }
                }
                if (live != null)
                {
                    liveResults.Remove(live.Producer);
                    if (live.Replacement != null)
                    {
                        var helper = live.Finish(LocalConsumer, refs);
                        BirGraphValue helperRoot = helper;
                        Walk(new BirGraphSlot(() => helperRoot, value => helperRoot = value),
                            ownerFrame, [], lexicalOwner, new());
                        if (host != null) host.Methods = host.Methods.OrEmpty().Append(helper).ToArray();
                        else file.Methods = file.Methods.OrEmpty().Append(helper).ToArray();
                    }
                }
                if (node.MethodWitnessFacts.HasValue && Invocation.Admits(node))
                {
                    resultScopes.Remove(node, out var projectedScope);
                    if (FixedIntersection(new Invocation(node), lexicalOwner, ownerFrame, refs, emittedLocalTypes, generated,
                        deferVariableArguments: !finishFixedCalls, classScope: projectedScope.Owner,
                        methodScope: projectedScope.Method, resultUse: resultUse, locations: projectedScope.Locations) is { } fixedCall)
                    { slot.Replace(fixedCall); return; }
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

        BirFunctionDeclaration LocalConsumer(Invocation site) =>
            (site.Node.SelectedGenericCallable ?? site.Node.DeclarationIdentityFact.OrDefault()) is string id &&
            site.Owner is TypeNode.Fqn owner && declarations.TryGetValue(id, out var matches)
                ? matches.SingleOrDefault(m => m.Owner == owner.Name && m.Method.Name.OrDefault() == site.Method)?.Method : null;

        // The caller's invariant container fixes an ordinary callee's method
        // argument. A dependent X : Y must follow the material frame of Y too;
        // neither the live list nor the selected MethodDef changes.
        internal bool BindMaterialArguments(BirGraphValue node, CallableWitnessScope owner, CallableWitnessScope method,
            ReferenceMetadataIndex refs)
        {
            if (!Invocation.Admits(node) || node.ApplicationTypeArguments.OrDefault() is not { Length: > 0 } actual ||
                !actual.Any(t => CallableWitnessScope.MaterialFact(t, owner, method) != null)) return false;
            var site = SelectedInvocation(new Invocation(node), out _);
            var local = LocalConsumer(site);
            var signature = local?.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).ToArray()
                ?? site.Member?.ParameterTypes.OrDefault();
            if (signature == null) return false;
            var result = local?.Ret.OrDefault() ?? site.Member?.ReturnType.OrDefault();
            var wanted = new HashSet<int>();
            foreach (var type in signature.Append(result))
                if (type != null) ProjectedMethodPlan.NativeUses(type, refs, (v, _, _, _) => {
                    if (v.Scope == "method" && CallableWitnessScope.MaterialFact(actual[v.I], owner, method) != null) wanted.Add(v.I);
                    return null;
                });
            if (wanted.Count == 0) return false;
            var frame = local?.TypeParams.OrEmpty() ?? site.SelectedFrame;
            if (frame == null && site.Member?.DeclaringType.OrDefault() is TypeNode.Fqn declaring &&
                TrySelectedMethod(site, declaring, refs, out var selected))
                frame = AddressTarget.ReadFrame(selected.GetGenericArguments());
            if (frame?.Length != actual.Length) return false;
            int count;
            do {
                count = wanted.Count;
                for (int i = 0; i < frame.Length; ++i)
                    if (CallableWitnessScope.MaterialFact(actual[i], owner, method) != null &&
                        frame[i].Constraints.OrEmpty().Any(bound => ContainsCapturedSlot(bound, wanted))) wanted.Add(i);
            } while (count != wanted.Count);
            var closed = actual.ToArray();
            foreach (var i in wanted) closed[i] = CallableWitnessScope.Material(actual[i], owner, method);
            node.ApplicationTypeArguments = closed;
            // A fixed CLR declaration cannot split its own MethodDef. If
            // its constraint is not proven by the material slot, close that
            // MethodDef at the existing checked boundary, not in open IL.
            var ownerArgs = ((site.Member?.DeclaringType.OrDefault() ?? site.Owner) as TypeNode.Fqn)?.Args ?? [];
            return wanted.Any(i => {
                var material = CallableWitnessScope.MaterialFact(actual[i], owner, method);
                return frame[i].SpecialConstraints.OrEmpty().Except(material.Formal.SpecialConstraints.OrEmpty())
                    .Any(s => s != "allowsRefStruct") || frame[i].Constraints.OrEmpty().Select(c =>
                    FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(c, ownerArgs, closed)).Any(c =>
                        c != closed[i] && !FBoundStarProjectionErasure.IsObjectish(c) && !material.Formal.Constraints.OrEmpty().Contains(c));
            });
        }

        internal bool NeedsNullableRecovery(BirGraphValue node, CallableWitnessScope owner, CallableWitnessScope method,
            ReferenceMetadataIndex references)
        {
            if (method.Representations == null && owner.CapturedNullable == null || !Invocation.Admits(node) ||
                node.ApplicationTypeArguments.OrDefault() is not { Length: > 0 } actual ||
                !actual.Any(t => CallableWitnessScope.LostNullableBounds(t, owner, method).Length != 0)) return false;
            var site = new Invocation(node);
            var id = node.SelectedGenericCallable ?? node.DeclarationIdentityFact.OrDefault();
            var local = LocalConsumer(site);
            var frame = local?.TypeParams.OrDefault()
                ?? (id != null ? references.PhysicalMethodTypeParametersInPhysicalFrame(id, nativeNames: true) : null);
            if (frame == null && site.Member?.DeclaringType.OrDefault() is TypeNode.Fqn native &&
                TrySelectedMethod(site, native, references, out var selected))
                frame = AddressTarget.ReadFrame(selected.GetGenericArguments());
            if (frame == null || frame.Length != actual.Length) return false;
            var ownerArgs = ((site.Member?.DeclaringType.OrDefault() ?? site.Owner) as TypeNode.Fqn)?.Args ?? [];
            var revised = actual;
            bool needsRecovery = false;
            for (var i = 0; i < actual.Length; i++)
            {
                var lost = CallableWitnessScope.LostNullableBounds(actual[i], owner, method);
                if (LostDependencies(frame[i], ownerArgs, actual, lost).FirstOrDefault() is not { } nullable) continue;
                var bounds = frame[i].Constraints.OrEmpty().Where(c => !FBoundStarProjectionErasure.IsObjectish(c)).ToArray();
                var operands = local != null ? local.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType)
                    : site.Member?.ParameterTypes.OrEmpty();
                var result = local?.Ret.OrDefault() ?? site.Member?.ReturnType.OrDefault();
                // With no input fixing X, an invariant CLR return can be
                // constructed with the value representation of X. For a
                // class-bounded source its own nullable witness is that same
                // representation; a nullable witness of a supertype is not.
                bool IsClass(TypeNode.Fqn b) => definitions.TryGetValue(b.Name, out var definition)
                    ? definition.Type.Kind.OrDefault() == "class" : references.IsClassType(b);
                // Only an erased, otherwise free argument can collapse onto the
                // upper witness. An invariant operand can fix it independently.
                if (bounds.Length != 1 || frame[i].SpecialConstraints.OrEmpty().Length != 0 || operands == null || result == null ||
                    operands.Any(t => ContainsCapturedSlot(t, [i])) ||
                    ContainsCapturedSlot(result, [i]) && CallableWitnessScope.ReferenceValue(actual[i], owner, method, IsClass) != nullable ||
                    frame.Any(p => p.Constraints.OrEmpty().Any(t => ContainsCapturedSlot(t, [i]))))
                {
                    needsRecovery = true;
                    continue;
                }
                if (ReferenceEquals(revised, actual)) revised = actual.ToArray();
                revised[i] = nullable;
            }
            if (!ReferenceEquals(revised, actual)) node.ApplicationTypeArguments = revised;
            return needsRecovery;
        }

        internal BirGraphValue FixedIntersection(Invocation call, string lexicalOwner, BirTypeParameter[] ownerFrame, ReferenceMetadataIndex refs,
            HashSet<string> emitted, List<IBirTypeDeclaration> generated, bool deferVariableArguments = false,
            CallableWitnessScope classScope = default, CallableWitnessScope methodScope = default,
            Func<string,string> allocate = null, IBirTypeDeclaration lexicalDefinition = null, LiveResultUse resultUse = null,
            bool projectingBody = false, IReadOnlyDictionary<string, TypeNode> locations = null, bool splitDependency = false)
        {
            var occurrence = call.Node;
            call = SelectedInvocation(call, out var access);
            var id = call.Node.SelectedGenericCallable ?? (splitDependency ? call.Node.DeclarationIdentityFact.OrDefault() : null);
            var owner = (TypeNode.Fqn)(call.Member?.DeclaringType.OrDefault() ?? call.Owner);
            var binding = id != null && declarations.TryGetValue(id, out var bindings)
                ? bindings.SingleOrDefault(b => b.Owner == owner.Name && b.Method.Name.OrDefault() == call.Method) : null;
            var local = binding?.Method;
            if (!splitDependency && !(id == null && call.Member != null) && call.Node is not (BirGenericInstance or BirGenericStatic) &&
                !ProjectedCallableEntries.HasFixedAbi(local, id, refs)) return null;
            if (projectingBody && LiveResultUse.InCurrentPass(occurrence, true) &&
                SignatureValueTypes.Of(call.Member.ReturnType.Value) is { } storage &&
                (storage is TypeNode.ByRef || ByRefLike(storage, refs)))
            {
                // This walk owns the split frame but not the consuming
                // expression. Keep its witnesses on the occurrence until
                // both the producer and its consumer can be closed together.
                HasDeferredFixedCalls = true;
                resultScopes[occurrence] = (classScope, methodScope, locations);
                return occurrence;
            }
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
                var definition = binding.Container.Type;
                var ownerParameters = definition == null ? [] : TypeParameterFrame.Declarations(definition).ToArray();
                var methodParameters = local.TypeParams.OrEmpty();
                bool Typed(TypeNode type) => SignatureValueTypes.Of(type) is TypeNode.Tv v
                    ? (v.Scope == "method" ? methodParameters : ownerParameters)[v.I].SpecialConstraints.OrEmpty().Contains("allowsRefStruct")
                    : HasAddress(type) || ByRefLike(SignatureValueTypes.Of(type), refs);
                var formals = local.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).ToArray();
                // A local MethodDef has no token yet. Bind its complete physical
                // descriptor. Zero is reserved for unique lexical thunks.
                target = new(local.Name.Value, -1, formals.Select(t => RuntimeKey(SignatureValueTypes.Of(t), refs)).ToArray(),
                    ownerParameters, methodParameters, definition?.Kind.Value == "struct", formals,
                    formals.Select(Typed).ToArray(), local.Ret.Value, Typed(local.Ret.Value));
            }
            var signature = target.Signature;
            var ret = target.Result;
            var actual = call.TypeArguments.Value;
            var proofs = occurrence.MethodWitnessFacts.OrEmpty();
            var fixedAddresses = FixedAddresses(signature, call.Arguments.OrDefault(), locations);
            var application = BindFixedWitnesses(target.MethodFrame, signature, owner.Args ?? [], actual, proofs, LexicalCandidates(actual, classScope, methodScope),
                classScope, methodScope, t => definitions.TryGetValue(t.Name, out var declaration)
                    ? declaration.Type.Kind.OrDefault() == "interface" : refs.IsInterfaceType(t), fixedAddresses);
            var unit = IsVoid(ret);
            var typed = target.ValueOwner && call.Instance || target.TypedParameters.Contains(true) || target.TypedResult;
            LexicalTarget lexical = null;
            var capture = TakeCaptureFacts(occurrence);
            var rows = capture?.Arguments.HasValue == true
                ? CaptureRows(capture, signature.Select(SignatureValueTypes.Of).ToArray(), refs) : [];
            // IKey<capture> is not IKey<object>. Keep the projected result
            // that belongs to this expression, not the fallback MethodSpec.
            var capturedResult = rows.Length == 0 ? null : capture.Result ?? BirNodeType.Of(occurrence as BirGraphNode);
            var result = capturedResult ?? FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(ret, owner.Args ?? [], actual);
            occurrence.MethodWitnessFacts = default;
            if (access == null && (call.Super || typed && lexicalAccess))
            {
                lexical = LexicalAdapter(call, target, owner, lexicalOwner, ownerFrame, local != null, typed, lexicalDefinition);
            }
            return typed || access != null ? ProjectAddressCall(call, target, owner, application.Facets, refs, allocate ?? (role => AllocateAdapter(lexicalOwner, emitted, role)), generated, lexical, access, application.Arguments,
                fixedAddresses, captures: rows, capturedResult: capturedResult, resultUse: resultUse)
                : IntersectionCall(call, lexical?.Owner ?? owner, lexical == null ? target.Token : 0,
                    lexical?.Method.Name.Value ?? target.Name, target.Keys, application.Arguments, application.Facets, result, unit, refs, rows);
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
            if (call.Instance && !local)
            {
                new Invocation(forward).Member.DeclaringType = target;
                if (forward is BirGenericInstance instance) instance.Type = target;
                if (forward is BirCallInstance virtualCall) virtualCall.OwnerType = target;
            }
            var method = Method(name, frame, parameters, result,
                Body(forward, IsVoid(result), objectResult: false), isStatic: !call.Instance, visibility: adapterAccess ? "internal" : "private");
            definition.Methods = definition.Methods.OrEmpty().Append(method).ToArray();
            return method;
        }

        static string AllocateAdapter(string lexicalOwner, HashSet<string> emitted, string role)
        {
            string name;
            do name = lexicalOwner + "$" + role + _next++; while (emitted.Contains(name));
            return name;
        }

        LexicalTarget LexicalAdapter(Invocation call, AddressTarget target, TypeNode.Fqn owner, string lexicalOwner,
            BirTypeParameter[] ownerFrame, bool local, bool typed, IBirTypeDeclaration definition = null)
        {
            TypeNode Close(TypeNode type) => SupertypeGraph.SubstOwnerTvs(type, owner.Args ?? []);
            var thunk = LexicalForwarder(call, owner, lexicalOwner, target.MethodFrame.Select(p => CloneParameter(p, Close)).ToArray(),
                target.Signature.Select(Close).ToArray(), Close(target.Result), local, typed, definition);
            return new(new(lexicalOwner, ownerFrame.Length == 0 ? null : Enumerable.Range(0, ownerFrame.Length)
                .Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray()), ownerFrame, thunk);
        }

        static void Declare(BirGraphValue node, Dictionary<string, TypeNode> scope)
        { if (node is BirVariable { Name.HasValue: true, Type.HasValue: true } variable) scope[variable.Name.Value] = variable.Type.Value; }

        static Dictionary<string, TypeNode> BodyScope(BirGraphValue owner, Dictionary<string, TypeNode> outer)
        {
            var inner = new Dictionary<string, TypeNode>(outer, StringComparer.Ordinal);
            var (id, type) = owner switch {
                BirCatch c => (c.Var.OrDefault(), c.ExcType.OrDefault()),
                BirForArray f => (f.Var.OrDefault(), f.Elem.OrDefault()),
                BirForEach f => (f.Var.OrDefault(), f.Elem.OrDefault()),
                BirFor f => (f.Var.OrDefault(), Int), BirForRange f => (f.Var.OrDefault(), Int),
                BirRepeat r => (r.Var.OrDefault(), Int), _ => (null, null),
            };
            if (id != null && type != null) inner[id] = type;
            return inner;
        }

        CaptureRow[] CaptureRows(CaptureFacts capture, TypeNode[] declarationParameters, ReferenceMetadataIndex refs)
        {
            (TypeNode Construction, bool StorageOnly)[] witnesses = capture.Target != null && _parameterWitnesses.TryGetValue(capture.Target, out var source)
                ? source.Select((t, i) => {
                    var marker = captureConstruction?.Invoke(t);
                    return (BirTypeLowering.LowerPhysicalType(marker ?? t, refs.Aliases, refs.IsValueType,
                        refs.PhysicalTypeNames, typeArg: false), marker == null && FBoundStarProjectionErasure.IsObjectish(declarationParameters[i]));
                }).ToArray()
                : refs.CapturedParameterWitnesses(capture.Target, declarationParameters, captureConstruction);
            var frame = capture.Target != null && _methodWitnessFrames.TryGetValue(capture.Target, out var localFrame)
                ? localFrame : refs.NullableMethodFrame(capture.Target);
            var rows = new List<CaptureRow>();
            foreach (var entry in capture.Arguments.Value)
            {
                var index = entry.Argument.Value;
                if (CaptureOwner(SignatureValueTypes.Of(witnesses[index].Construction)) is not { Args: { } parts } owner)
                    throw new InvalidOperationException("Captured argument has no declaration-owned construction witness");
                var localOwner = definitions.GetValueOrDefault(owner.Name);
                var head = new TypeNode.Fqn(localOwner != null ? owner.Name : PhysicalName(owner, refs));
                var ownerFrame = localOwner != null
                    ? NullableRepresentationViews.TypeFacts((BirGraphValue)localOwner.Type)?.NullableFrame
                    : refs.NullableTypeFrames.GetValueOrDefault(owner.Name);
                foreach (var position in entry.OwnerPositions.Value)
                {
                    // Other occurrences of this method variable need not be invariant.
                    // Only the projected owner position supplies the runtime witness.
                    var pos = ownerFrame?.SourcePosition(position) ?? position;
                    if (parts[pos] is not TypeNode.Tv { Scope: "method" } variable) continue;
                    var logical = frame == null ? variable.I : frame.SourceIndex(variable.I);
                    if (logical is not int sourceSlot) continue;
                    var nullable = frame?.NullableIndices.Contains(sourceSlot) == true
                        ? frame.NullableVariable(new TypeNode.Tv("method", sourceSlot)).I : -1;
                    var kind = !witnesses[index].StorageOnly ? WitnessSource.Nominal :
                        entry.AppliedPositions.OrDefault()?.Contains(position) == true ? WitnessSource.Construction : WitnessSource.Storage;
                    rows.Add(new(index, head, pos, variable.I, nullable, kind));
                }
            }
            // A declaration transported as object accepts the existing value independently
            // of its closed backing type. Its recovered construction can supply an otherwise
            // unknown slot, but cannot constrain a slot witnessed by a nominal parameter.
            // A generic construction is not an independent constraint on its
            // input. When it is the *only* source, its real storage is still
            // observable by a native use of the closed method argument.
            return rows.OrderBy(row => row.Source).ToArray();
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
            // Check the actual call signature: a bare native method variable can
            // close over a Span, which cannot be put in the reflection argument array.
            bool RequiresTypedTransport(TypeNode formal)
            {
                var type = formal is TypeNode.Tv
                    ? FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(formal, owner.Args ?? [], call.TypeArguments.Value)
                    : formal;
                return HasAddress(type) || ByRefLike(type, refs) || type is TypeNode.Tv v &&
                    (v.Scope == "method" ? methodFrame : ownerFrame)[v.I].SpecialConstraints.OrEmpty().Contains("allowsRefStruct");
            }
            var typedInput = declarationParameters.Any(RequiresTypedTransport);
            // Independent managed references can travel through the typed adapter unchanged.
            // The captured-slot check below still excludes a reference whose pointee would change.
            if (RequiresTypedTransport(declaredReturn) || typedInput && (local || declarationParameters.Any(t => t is TypeNode.Ptr) ||
                captureIndices.Any(i => RequiresTypedTransport(declarationParameters[i])))) return call.Node;
            // Closing the method cannot convert an already-created invariant
            // construction into the captured construction. Do not replace that
            // existing edge with a verifier-green but uncallable thunk.
            var rows = CaptureRows(capture, declarationParameters, refs);
            if (rows.Length == 0) return call.Node;
            var closedSlots = rows.SelectMany(row => new[] { row.Slot, row.Nullable }).Where(i => i >= 0).ToArray();
            // Null can inhabit a reference construction for every closing of its arguments.
            // A bare captured variable is different: reflection silently turns null into
            // default(T) for a non-nullable value type.
            static bool IsNullLiteral(IBirExpression operand) => operand is BirConstant c
                && (c.Value.IsNull || c.Value.OrDefault() is BirLiteral.NullLiteral);
            bool AcceptsNullConstruction(TypeNode formal) => formal switch {
                TypeNode.Oblivious o => AcceptsNullConstruction(o.Of),
                TypeNode.Nullable n => n.Of is TypeNode.Fqn || AcceptsNullConstruction(n.Of),
                TypeNode.Array or TypeNode.Fn => true,
                TypeNode.Fqn f => !(definitions.TryGetValue(f.Name, out var d)
                    ? d.Type.Kind.OrDefault() is "struct" or "enum" : refs.IsValueType(f)),
                _ => false,
            };
            List<int> scalarNulls = null;
            for (var i = 0; i < arguments.Length; i++)
            {
                if (!captureIndices.Contains(i) && ContainsCapturedSlot(declarationParameters[i], closedSlots)
                    && !(IsNullLiteral(arguments[i]) && AcceptsNullConstruction(declarationParameters[i])))
                {
                    if (local || !IsNullLiteral(arguments[i]) || declarationParameters[i] is not TypeNode.Tv { Scope: "method" })
                        return call.Node;
                    (scalarNulls ??= []).Add(i);
                }
            }

            if (typedInput || scalarNulls != null)
            {
                // Close the invariant operand from its runtime witness, but pass
                // independent stack-only values through the typed Invoke signature.
                if (!TrySelectedMethod(call, owner, refs, out var method))
                    throw new InvalidOperationException("Captured invocation lost its selected MethodDef");
                var target = AddressTarget.FromReference(method, call.Member, selectedFrame);
                constraintValidation.ValidateUncapturedArguments(call.Node, closedSlots, ownerFrame, methodFrame);
                LexicalTarget lexical = null;
                if (!method.IsPublic || call.Super)
                {
                    if (call.Instance && !call.Super && call.Receiver is not BirThis)
                    {
                        // A nested caller can invoke a family member on its enclosing derived
                        // instance. The instance forwarder belongs to that receiver, not the caller.
                        var receiver = (TypeNode.Fqn)physicalType((BirGraphValue)call.Receiver, scope);
                        var definition = definitions[receiver.Name].Type;
                        var frame = TypeParameterFrame.Declarations(definition).ToArray();
                        var open = new TypeNode.Fqn(receiver.Name, frame.Length == 0 ? null : Enumerable.Range(0, frame.Length)
                            .Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
                        var declaring = SupertypeGraph.ReachableFromClass(open, classGraph.Value, refs)
                            .First(e => !e.isInterface && SupertypeGraph.SameDeclaration(e.spec,owner,refs.PhysicalTypeNames)).spec;
                        lexical = LexicalAdapter(call, target, declaring, receiver.Name, frame, false, true, definition) with { Owner = receiver };
                    }
                    else lexical = LexicalAdapter(call, target, owner, lexicalOwner, ownerFrame, false, true);
                }
                var projected = ProjectAddressCall(call, target, owner, [], refs, role => AllocateAdapter(lexicalOwner, emitted, role), generated, lexical, null,
                    call.TypeArguments.Value, captures: rows, capturedResult: IsVoid(declaredReturn) ? null : result, scalarNulls: scalarNulls);
                return IsVoid(declaredReturn) && result != null && !IsVoid(result)
                    ? new BirValueBlock { Type = result, Stmts = new IBirStatement[] { new BirExpressionStatement {
                        Expr = OptionalField<IBirExpression>.Of((IBirExpression)projected) } }, Result = ClrMemberResolution.UnitSingletonRead(emitted, refs) }
                    : projected;
            }

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
            // These slots now bind to the actual CLR container's element,
            // not the erased Kotlin star at the original call site.
            ForgetReopenedMaterial(forwardCall, closedSlots.Contains);
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
                CaptureOwners(rows), CaptureCoordinates(rows, call.Instance ? 1 : 0), NewArray(Object, values) };
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
                new TypeNode.Array(runtimeType), new TypeNode.Nullable(new TypeNode.Array(runtimeType)),
                new TypeNode.Nullable(new TypeNode.Fqn("kotlin.IntArray")), new TypeNode.Array(new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"))) };
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
