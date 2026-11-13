using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using RepresentationRole = DotKt.Bir.NullableRepresentationFrame.Role;
using OwnerView = NullableRepresentationViews.Owner;
using MethodView = NullableRepresentationViews.Method;

static partial class NullableRepresentationDemand
{
    internal sealed record SourceMethodDemand(MethodView View, Variables Signature, Variables Body, bool IsLocal = false)
    {
        internal BirGraphValue Declaration => View.Node;
        internal BirInheritedImplementation Implementation => View.Implementation;
        internal BirTypeParameter[] TypeParameters => View.Parameters.OrEmpty();
        IEnumerable<int> Indices(RepresentationRole role) => Signature.For("method", role)
            .Concat(View.CanExtendBodyFrame ? Body.For("method", role) : Enumerable.Empty<int>()).Distinct().OrderBy(index => index);
        internal NullableRepresentationFrame Frame => new(TypeParameters.Length, Indices(RepresentationRole.Nullable),
            storageIndices: Indices(RepresentationRole.Storage), nullableStorageIndices: Indices(RepresentationRole.NullableStorage));
    }

    internal sealed record SourceOwnerDemand(OwnerView View, Variables Signature, Variables Body, List<SourceMethodDemand> Methods)
    {
        internal BirGraphValue Declaration => View.Node;
        internal bool IsTypeDeclaration => View.IsRefCell || View.Kind != null;
        internal SourceOwnerDemand CapturedOwner { get; set; }
        internal int CaptureOffset { get; set; }
        internal NullableRepresentationFrame Frame {
            get {
                var enclosing = CapturedOwner?.Frame;
                IEnumerable<int> Indices(RepresentationRole role) => Signature.For("type", role).Concat(Body.For("type", role))
                    .Concat(Methods.SelectMany(method => method.Signature.For("type", role).Concat(method.Body.For("type", role))))
                    .Concat(enclosing?.Companions.Where(slot => slot.Representation == role)
                        .Select(slot => CaptureOffset + slot.SourceIndex) ?? Enumerable.Empty<int>())
                    .Distinct().OrderBy(index => index);
                var frame = new NullableRepresentationFrame(View.Parameters.OrEmpty().Length, Indices(RepresentationRole.Nullable),
                    storageIndices: Indices(RepresentationRole.Storage), nullableStorageIndices: Indices(RepresentationRole.NullableStorage));
                return enclosing == null ? frame : frame.WithEnclosingPrefix(enclosing, CaptureOffset);
            }
        }
    }

    public static IReadOnlyList<SourceOwnerDemand> Collect(IReadOnlyList<BirFileDeclaration> roots,
        IReadOnlyDictionary<string, NullableRepresentationFrame> referencedTypes = null,
        IReadOnlyDictionary<string, NullableRepresentationFrame> referencedMethods = null,
        GenericRepresentationPolicy policy = null, InnerApplicationFrames applicationFrames = null,
        IReadOnlyDictionary<string, NullableRepresentationFrame> declaredMethods = null)
    {
        var localBindings = BindLocalFunctions(roots);
        var owners = new List<SourceOwnerDemand>();
        foreach (var view in NullableRepresentationViews.Owners(roots))
        {
            var methods = view.Methods.OrEmpty().Select(method => new SourceMethodDemand(new MethodView(method), new(), new()))
                .Concat(view.Inherited.Select(method => new SourceMethodDemand(method, new(), new()))).ToList();
            void DiscoverLocals(BirGraphValue node, bool ownRoot = false)
            {
                if (node is BirAttribute or IBirExecutablePayload) return;
                if (node is BirLocalFunction { Decl.HasValue: true } local)
                    methods.Add(new SourceMethodDemand(new MethodView(local.Decl.Value), new(), new(), IsLocal: true));
                node.VisitChildren(child => {
                    if (!ownRoot || child is not (IBirTypeDeclaration or BirRefTypeDeclaration)) DiscoverLocals(child);
                });
            }
            DiscoverLocals(view.Node, ownRoot: true);
            owners.Add(new SourceOwnerDemand(view, new(), new(), methods));
        }
        var declarations = owners.Where(owner => owner.IsTypeDeclaration).ToDictionary(owner => owner.View.Name, StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            var captured = owner.View.CaptureCount;
            if (captured == 0 || owner.View.SemanticOwner is not string parentName || !declarations.TryGetValue(parentName, out var parent)) continue;
            var offset = owner.View.CaptureOffset;
            if (captured != parent.View.Parameters.OrEmpty().Length || offset < 0 || offset + captured > owner.View.Parameters.OrEmpty().Length)
                throw new InvalidOperationException("Semantic owner capture does not match source generic frame");
            owner.CapturedOwner = parent;
            owner.CaptureOffset = offset;
        }
        foreach (var owner in owners)
        {
            var seen = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
            for (var cursor = owner; cursor != null; cursor = cursor.CapturedOwner)
                if (!seen.Add(cursor.Declaration)) throw new InvalidOperationException("Cyclic semantic owner capture");
        }
        foreach (var method in owners.SelectMany(owner => owner.Methods).Where(method => !method.IsLocal))
            if (method.View.Id is string id && declaredMethods?.GetValueOrDefault(id) is { } declared)
            {
                if (declared.SourceArity != method.TypeParameters.Length)
                    throw new InvalidOperationException("Published method frame has a different source arity");
                foreach (var slot in declared.Companions) method.Signature.Add(new TypeNode.Tv("method", slot.SourceIndex), slot.Representation);
            }
        bool changed;
        do
        {
            var types = new Dictionary<string, NullableRepresentationFrame>(referencedTypes
                ?? new Dictionary<string, NullableRepresentationFrame>(), StringComparer.Ordinal);
            var methods = new Dictionary<string, NullableRepresentationFrame>(referencedMethods
                ?? new Dictionary<string, NullableRepresentationFrame>(), StringComparer.Ordinal);
            foreach (var owner in owners)
            {
                if (owner.IsTypeDeclaration) types[owner.View.Name] = owner.Frame;
                foreach (var method in owner.Methods) if (method.View.Id is string id) methods[id] = method.Frame;
            }
            var locals = new Dictionary<BirGraphValue, NullableRepresentationFrame>(ReferenceEqualityComparer.Instance);
            foreach (var method in owners.SelectMany(owner => owner.Methods).Where(method => method.IsLocal)) locals.Add(method.Declaration, method.Frame);
            var localFrames = new Dictionary<BirGraphValue, NullableRepresentationFrame>(ReferenceEqualityComparer.Instance);
            foreach (var (use, declaration) in localBindings) localFrames.Add(use, locals[declaration]);
            if (applicationFrames != null) types = applicationFrames.Project(types);
            var before = Count(owners);
            foreach (var owner in owners)
            {
                void Type(TypeNode value, Variables target, bool argument = false) { if (value != null) Visit(value, argument, target, types, policy); }
                void Graph(BirGraphValue value, Variables target) { if (value != null) Scan(value, target, types, methods, localFrames, policy: policy); }
                Type(owner.View.Base, owner.Signature);
                foreach (var edge in owner.View.Interfaces) Type(edge, owner.Signature);
                foreach (var parameter in owner.View.Parameters.OrEmpty()) Graph(parameter, owner.Signature);
                if (owner.Declaration is BirRefTypeDeclaration cell)
                {
                    if (policy?.OwnsValueSlots(owner.View.Name) == true && !policy.IsNativeField(cell) && cell.Elem.OrDefault() is TypeNode.Tv variable)
                        owner.Signature.Add(variable, RepresentationRole.Storage);
                    Type(cell.Elem.OrDefault(), owner.Signature, policy?.IsNativeField(cell) == true);
                }
                foreach (var slot in owner.View.Fields.Cast<BirGraphValue>().Concat(owner.View.Properties))
                {
                    var (type, initializer) = slot switch {
                        BirFieldDeclaration field => (field.Type.OrDefault(), field.Init.OrDefault()),
                        BirPropertyDeclaration property => (property.Type.OrDefault(), (IBirExpression)null),
                        _ => throw new InvalidOperationException(),
                    };
                    if (policy?.OwnsValueSlots(owner.View.Name) == true && !policy.IsNativeField(slot) && type is TypeNode.Tv variable)
                        owner.Signature.Add(variable, RepresentationRole.Storage);
                    Type(type, owner.Signature, policy?.IsNativeField(slot) == true);
                    Graph((BirGraphValue)initializer, owner.Body);
                }
                foreach (var constructor in owner.View.Constructors)
                {
                    foreach (var parameter in constructor.Params.OrEmpty()) Graph(parameter, owner.Signature);
                    foreach (var statement in constructor.Body.OrEmpty()) Graph((BirGraphValue)statement, owner.Body);
                }
                foreach (var method in owner.Methods)
                {
                    if (method.View.OwnsValueSlots(policy))
                    {
                        void DemandValue(TypeNode value) { if (value is TypeNode.Tv { Scope: "method" } variable) method.Signature.Add(variable, RepresentationRole.Storage); }
                        foreach (var parameter in method.View.ValueParameters) { DemandValue(parameter.Type.OrDefault()); DemandValue(parameter.KotlinType.OrDefault()); }
                        DemandValue(method.View.Return);
                        DemandValue(method.View.SourceReturn);
                    }
                    if (method.View.Function != null || method.Declaration is BirInheritedClassMethod)
                        foreach (var parameter in method.View.ValueParameters) Graph(parameter, method.Signature);
                    else foreach (var parameter in method.View.SignatureParameters) Type(parameter, method.Signature);
                    Type(method.View.Return, method.Signature, method.View.Function?._preparedDelegateReturnArgument.OrDefault() == true);
                    if (!method.View.Inherited)
                        foreach (var parameter in method.TypeParameters) Graph(parameter, method.Signature);
                    else
                    {
                        var constraints = new Variables();
                        foreach (var parameter in method.TypeParameters) Graph(parameter, constraints);
                        foreach (var role in CompanionRoles) method.Signature.For("method", role).UnionWith(constraints.For("method", role));
                    }
                    foreach (var statement in method.View.Body ?? Array.Empty<IBirStatement>()) Graph((BirGraphValue)statement, method.Body);
                }
            }
            foreach (var owner in owners.Where(owner => owner.CapturedOwner != null))
                foreach (var index in owner.Frame.NullableIndices)
                    if (index >= owner.CaptureOffset && index < owner.CaptureOffset + owner.CapturedOwner.View.Parameters.OrEmpty().Length)
                        owner.CapturedOwner.Body.Type.Add(index - owner.CaptureOffset);
            changed = Count(owners) != before;
        } while (changed);
        return owners;
    }

    internal static IReadOnlyDictionary<BirGraphValue, BirFunctionDeclaration> BindLocalFunctions(IReadOnlyList<BirFileDeclaration> roots)
    {
        var result = new Dictionary<BirGraphValue, BirFunctionDeclaration>(ReferenceEqualityComparer.Instance);
        foreach (var root in roots)
        {
            var declarations = new Dictionary<string, BirFunctionDeclaration>(StringComparer.Ordinal);
            var uses = new List<(BirGraphValue Node, string Id)>();
            void Walk(BirGraphValue node)
            {
                if (node is BirAttribute or IBirExecutablePayload) return;
                var id = node switch {
                    BirLocalFunction value => value.Id.OrDefault(), BirCallLocal value => value.Id.OrDefault(),
                    BirLocalFunctionReference value => value.Id.OrDefault(), _ => null,
                };
                if (node is BirLocalFunction or BirCallLocal or BirLocalFunctionReference)
                {
                    if (id == null) throw new InvalidOperationException("Local function edge has no lexical identity");
                    uses.Add((node, id));
                    if (node is BirLocalFunction { Decl.HasValue: true } declaration && !declarations.TryAdd(id, declaration.Decl.Value))
                        throw new InvalidOperationException("Duplicate local function identity: " + id);
                }
                node.VisitChildren(Walk);
            }
            Walk(root);
            foreach (var use in uses) result.Add(use.Node, declarations[use.Id]);
        }
        return result;
    }

    static int Count(IEnumerable<SourceOwnerDemand> owners) => owners.Sum(owner => owner.Signature.Count + owner.Body.Count
        + owner.Methods.Sum(method => method.Signature.Count + method.Body.Count));

    static void Scan(BirGraphValue node, Variables result, IReadOnlyDictionary<string, NullableRepresentationFrame> types,
        IReadOnlyDictionary<string, NullableRepresentationFrame> methods, IReadOnlyDictionary<BirGraphValue, NullableRepresentationFrame> locals,
        GenericRepresentationPolicy policy = null)
    {
        if (node is IBirExecutablePayload) return;
        if (locals.TryGetValue(node, out var localFrame))
        {
            var arguments = node is BirLocalFunction local ? local.Decl.Value._syntheticTypeArgs.OrDefault() : node.ApplicationTypeArguments.OrDefault();
            if (localFrame.RequiresMetadata)
            {
                if (arguments?.Length != localFrame.SourceArity) throw new InvalidOperationException("Local function arguments do not match their declaration frame");
                foreach (var slot in localFrame.Companions)
                    if (node is not BirLocalFunction || arguments[slot.SourceIndex] is TypeNode.Tv { Scope: "type" })
                        RequireRepresentation(arguments[slot.SourceIndex], slot.Representation, result);
            }
            if (node is BirLocalFunction) return;
        }
        if (node is BirGraphNode && node.DeclarationIdentityFact.OrDefault() is string id
            && methods.TryGetValue(id, out var frame) && frame.RequiresMetadata)
        {
            var arguments = node.ApplicationTypeArguments.OrDefault();
            if (arguments?.Length != frame.SourceArity) throw new InvalidOperationException("Generic call does not match its declared nullable frame");
            foreach (var slot in frame.Companions) RequireRepresentation(arguments[slot.SourceIndex], slot.Representation, result);
        }
        var snapshot = NullableRepresentationViews.ReturnFacts(node);
        node.RewriteRepresentationInPlace((ownership, role, type) => {
            if (NullableRepresentationViews.Frozen(ownership) || role is BirRepresentationRole.Declaration
                or BirRepresentationRole.DeclaredMemberType or BirRepresentationRole.TypeParameterDeclarations or BirRepresentationRole.Foreign
                || role == BirRepresentationRole.Return && snapshot.DeclarationReturn) return type;
            var nativeRead = role is BirRepresentationRole.Return or BirRepresentationRole.ResultStamp
                && node is BirFieldGet or BirStaticFieldGet or BirLateinitGet && policy?.IsNativeField(node) == true;
            Visit(type, role == BirRepresentationRole.TypeArguments || nativeRead
                || role == BirRepresentationRole.Element && NullableGenericErasure.IsArgumentElementKind((node as BirGraphNode)?.Tag),
                result, types, policy);
            return type;
        }, (ownership, role, child) => {
            if (!NullableRepresentationViews.Frozen(ownership) && ownership != BirGenericFrameRole.OverrideDeclaration
                && role is not (BirRepresentationRole.Attributes or BirRepresentationRole.InheritedImplementation
                    or BirRepresentationRole.Declaration or BirRepresentationRole.TypeParameterDeclarations))
                Scan(child, result, types, methods, locals, policy);
        });
    }
}
