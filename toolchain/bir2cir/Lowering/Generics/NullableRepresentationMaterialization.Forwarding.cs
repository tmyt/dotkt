using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableRepresentationMaterialization
{
    // A storage companion is not constrained merely because its ordinary counterpart is.
    // If that very slot is passed to a constrained formal, forward only the interface
    // evidence required there and available from the declared storage bound.
    static void ForwardStorageProofs(IReadOnlyList<NullableRepresentationDemand.SourceOwnerDemand> owners,
        IReadOnlyDictionary<BirGraphValue, NullableRepresentationFrame> methodFrames,
        IReadOnlyDictionary<BirGraphValue, NullableRepresentationFrame> ownerFrames,
        IReadOnlyDictionary<BirGraphValue, BirFunctionDeclaration> localBindings,
        IReadOnlyList<(NullableRepresentationViews.Owner Owner, BirFunctionDeclaration Method, NullableRepresentationFrame Frame)> helpers,
        IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex references, GenericRepresentationPolicy policy)
    {
        if (policy == null) return;
        var allowed = new Dictionary<BirTypeParameter, TypeNode[]>(ReferenceEqualityComparer.Instance);
        var methods = new Dictionary<string, BirTypeParameter[]>(StringComparer.Ordinal);
        var types = new Dictionary<string, BirTypeParameter[]>(StringComparer.Ordinal);
        void Register(BirTypeParameter[] rows, NullableRepresentationFrame frame, string scope)
        {
            foreach (var index in frame.StorageIndices)
            {
                var row = rows[frame.Variable(new TypeNode.Tv(scope, index), NullableRepresentationFrame.Role.Storage).I];
                if (row.ErasedInnerConstraints.OrEmpty() is not { Length: > 0 } bounds) continue;
                var proofs = new BirTypeParameter();
                policy.SetStorageBounds(proofs, bounds);
                var missing = proofs.Constraints.OrEmpty().Except(row.Constraints.OrEmpty()).ToArray();
                if (missing.Length != 0) allowed[row] = missing;
            }
        }
        foreach (var owner in owners)
        {
            var ownerRows = owner.View.Parameters.OrEmpty();
            if (owner.IsTypeDeclaration) types[owner.View.Name] = ownerRows;
            Register(ownerRows, ownerFrames[owner.Declaration], "type");
            foreach (var method in owner.Methods)
            {
                var rows = method.View.Parameters.OrEmpty();
                if (method.View.Id is string id) methods[id] = rows;
                if (method.View.CanExtendBodyFrame) Register(rows, methodFrames[method.Declaration], "method");
            }
        }
        foreach (var (_, method, frame) in helpers)
        {
            var rows = method.TypeParams.OrEmpty();
            methods.Add(method.DeclarationId.Value, rows);
            Register(rows, frame, "method");
        }
        if (allowed.Count == 0) return;
        var order = new InnerApplicationOrder(roots, references);
        var users = new Dictionary<BirTypeParameter, List<(BirTypeParameter Actual, TypeNode[] Owner, TypeNode[] Method)>>(ReferenceEqualityComparer.Instance);
        void Scan(BirGraphValue root, BirTypeParameter[] ownerRows, BirTypeParameter[] methodRows)
        {
            if (!ownerRows.Any(allowed.ContainsKey) && !methodRows.Any(allowed.ContainsKey)) return;
            void Connect(TypeNode[] args, BirTypeParameter[] formals, TypeNode[] ownerArgs, TypeNode[] methodArgs)
            {
                if (args == null || formals == null || args.Length != formals.Length) return;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] is not TypeNode.Tv v) continue;
                    var rows = v.Scope == "method" ? methodRows : ownerRows;
                    if ((uint)v.I >= (uint)rows.Length || !allowed.ContainsKey(rows[v.I])) continue;
                    if (!users.TryGetValue(formals[i], out var edges)) users[formals[i]] = edges = [];
                    edges.Add((rows[v.I], ownerArgs, methodArgs));
                }
            }
            void Application(TypeNode type)
            {
                if (type is not TypeNode.Fqn { Args.Length: > 0 } f || !f.Args.Any(t => t is TypeNode.Tv)) return;
                if (!types.TryGetValue(f.Name, out var rows))
                    types[f.Name] = rows = references?.NullableTypeFrames.ContainsKey(f.Name) == true
                        ? references.PhysicalOwnerTypeParametersInPhysicalFrame(f.Name, nativeNames: true) : null;
                if (rows?.Length != f.Args.Length) return;
                var args = order.DeclarationArguments(f);
                Connect(args, rows, args, []);
            }
            void Visit(BirGraphValue node)
            {
                if (node is BirAttribute or IBirExecutablePayload || node != root &&
                    (ownerFrames.ContainsKey(node) || methodFrames.ContainsKey(node))) return;
                if (node.ApplicationTypeArguments.OrDefault() is { Length: > 0 } args)
                {
                    if (localBindings.TryGetValue(node, out var local))
                        Connect(args, local.TypeParams.OrEmpty(), [], args);
                    else if (node.DeclarationIdentityFact.OrDefault() is string id)
                    {
                        if (!methods.TryGetValue(id, out var formals))
                            methods[id] = formals = references?.PhysicalMethodTypeParametersInPhysicalFrame(id, nativeNames: true);
                        if (formals?.Length == args.Length)
                            Connect(args, formals, order.DeclarationArguments(SelectedOwner(node) as TypeNode.Fqn) ?? [], args);
                    }
                }
                var snapshot = NullableRepresentationViews.ReturnFacts(node);
                bool Lexical(BirGenericFrameRole ownership) => !NullableRepresentationViews.Frozen(ownership)
                    && ownership is not (BirGenericFrameRole.OverrideDeclaration or BirGenericFrameRole.SyntheticApplication);
                node.RewriteRepresentationInPlace((ownership, role, type) => {
                    if (Lexical(ownership) && !(role == BirRepresentationRole.Return && snapshot.DeclarationReturn)
                        && role is not (BirRepresentationRole.Declaration
                        or BirRepresentationRole.DeclaredMemberType or BirRepresentationRole.TypeParameterDeclarations
                        or BirRepresentationRole.Foreign))
                        BirGraphNameFields.VisitTypeNodes(type, Application);
                    return type;
                }, (ownership, role, child) => {
                    if (Lexical(ownership) && role is not (BirRepresentationRole.Attributes
                        or BirRepresentationRole.InheritedImplementation or BirRepresentationRole.TypeParameterDeclarations)
                        && !(ownerFrames.ContainsKey(node) && child is BirFunctionDeclaration)) Visit(child);
                });
            }
            Visit(root);
        }
        foreach (var owner in owners)
        {
            var rows = owner.View.Parameters.OrEmpty();
            Scan(owner.Declaration, rows, []);
            foreach (var method in owner.Methods)
                if (method.View.Function is { } function) Scan(function, rows, method.View.Parameters.OrEmpty());
        }
        foreach (var (owner, method, _) in helpers)
            Scan(method, owner.Parameters.OrEmpty(), method.TypeParams.OrEmpty());
        var queue = new Queue<BirTypeParameter>(users.Keys.Where(row => row.Constraints.OrEmpty().Length != 0));
        var queued = new HashSet<BirTypeParameter>(queue, ReferenceEqualityComparer.Instance);
        while (queue.TryDequeue(out var formal))
        {
            queued.Remove(formal);
            foreach (var (actual, ownerArgs, methodArgs) in users[formal])
            {
                var existing = actual.Constraints.OrEmpty();
                var required = formal.Constraints.OrEmpty().Select(bound =>
                    FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(bound, ownerArgs, methodArgs));
                var add = allowed[actual].Intersect(required).Except(existing).ToArray();
                if (add.Length == 0) continue;
                actual.Constraints = existing.Concat(add).ToArray();
                if (users.ContainsKey(actual) && queued.Add(actual)) queue.Enqueue(actual);
            }
        }
    }
}
