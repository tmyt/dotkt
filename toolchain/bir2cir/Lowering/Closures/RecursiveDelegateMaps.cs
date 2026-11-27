using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A recursive Invoke can change D<T> into D<Func<T>>. Specializing its
// invocation body at each closed construction would enumerate infinitely many
// signatures. Name the adapter by its open declaration instead; pass conversions
// of its varying arguments as ordinary typed functions.
sealed class RecursiveDelegateMaps(IReadOnlySet<string> localTypes, ReferenceMetadataIndex refs, Func<string, string> fresh)
{
    readonly Dictionary<Type, bool> recursion = new();
    readonly Dictionary<(Type, string), string> templates = new();
    BirFileDeclaration file;
    string scalar;
    string Fresh() => fresh(string.Concat((file.FileClass.OrDefault() ?? "File").Select(c => char.IsLetterOrDigit(c) ? c : '_')));

    bool Recursive(Type root)
    {
        if (recursion.TryGetValue(root, out var known)) return known;
        var active = new HashSet<Type>(); var done = new HashSet<Type>();
        bool Visit(Type definition)
        {
            if (active.Contains(definition)) return true;
            if (!done.Add(definition)) return false;
            active.Add(definition);
            var invoke = definition.GetMethod("Invoke");
            bool Child(Type type) => type.IsConstructedGenericType && type.ContainsGenericParameters
                && ReferenceMetadataReader.IsDelegate(type) &&
                (Visit(type.GetGenericTypeDefinition()) || type.GetGenericArguments().Any(Child));
            var cycle = Child(invoke.ReturnType) || invoke.GetParameters().Any(p => Child(p.ParameterType));
            active.Remove(definition);
            return cycle;
        }
        return recursion[root] = Visit(root);
    }

    internal IBirExpression TryMap(BirFileDeclaration document, TypeNode.Fqn from, TypeNode.Fqn to, IBirExpression original)
    {
        var definition = ClrMemberResolution.ResolveOwnerType(from, refs);
        if (!Recursive(definition)) return null;
        if (file != document) { file = document; templates.Clear(); scalar = null; }
        return Apply(Mapper(from, to, new()), from, to, original);
    }

    static TypeNode.Tv Tv(int i) => new("type", i);
    static TypeNode.Fqn FuncOf(TypeNode a, TypeNode b) => new("System.Func`2", [a, b]);
    static BirLocal Local(string name) => new() { Name = name };
    static IBirExpression Cast(TypeNode t, IBirExpression e) => new BirCast { Type = t, E = OptionalField<IBirExpression>.Of(e) };
    static BirReturn Return(IBirExpression e) => new() { Value = OptionalField<IBirExpression>.Of(e) };
    static BirConstant Null() => new() { Type = new TypeNode.Fqn("System.Object"), Value = new BirLiteral.NullLiteral() };
    static string P(int i) => "p" + i;
    static BirFieldGet Field(string owner, string name) => new() { OwnerType = new TypeNode.Fqn(owner), Recv = new BirThis(), Name = name };

    IBirExpression Close(string name, TypeNode[] args, TypeNode.Fqn slot, params IBirExpression[] captures) => new BirNewClosure
    {
        ClosureType = new TypeNode.Fqn(name),
        Method = "invoke",
        TypeArgs = args,
        FuncType = slot,
        Captures = captures,
        DelegateCtorRef = BirMemberReference.FromPhysical(ClrMemberResolution.PhysicalDelegateMember(localTypes, refs, slot, true)),
    };
    (TypeNode ret, TypeNode[] ps, BirMemberReference member) Signature(TypeNode.Fqn owner)
    {
        var member = ClrMemberResolution.PhysicalDelegateMember(localTypes, refs, owner, false);
        TypeNode Sub(TypeNode type) => SupertypeGraph.SubstOwnerTvs(type, owner.Args ?? []);
        return (Sub(member.ReturnType), member.ParameterTypes.Select(Sub).ToArray(), BirMemberReference.FromPhysical(member));
    }
    IBirExpression Call(TypeNode.Fqn owner, IBirExpression recv, params IBirExpression[] args)
    {
        var (ret, ps, member) = Signature(owner);
        return new BirNativeInstance
        {
            Type = owner,
            Method = "Invoke",
            Dispatch = "callvirt",
            Recv = OptionalField<IBirExpression>.Of(recv),
            Ret = ret,
            ArgTypes = ps,
            MemberRef = member,
            Args = args
        };
    }
    IBirExpression Apply(IBirExpression mapper, TypeNode from, TypeNode to, IBirExpression value) =>
        Call(FuncOf(from, to), mapper, value);

    void Class(string name, BirTypeParameter[] frame, (string name, TypeNode type)[] fields,
        TypeNode[] parameters, TypeNode result, IBirStatement[] body, bool native = false)
    {
        var type = new BirClassDefinition
        {
            Name = name,
            Kind = "class",
            Generated = true,
            TypeParams = frame.Select(p => (BirTypeParameter)p.DeepClone()).ToArray(),
            Base = OptionalField<TypeNode>.Null,
            Interfaces = Array.Empty<TypeNode>(),
            Fields = fields.Select(f => new BirFieldDeclaration { Name = f.name, Type = f.type }).ToArray(),
            Ctors = new[] { new BirConstructor {
                Params = fields.Select(f => new BirParameter { Name = f.name, Type = f.type }).ToArray(),
                BaseArgs = OptionalField<IBirExpression[]>.Null, Body = fields.Select(f => (IBirStatement)new BirSetField {
                    OwnerType = new TypeNode.Fqn(name), Recv = new BirThis(), Name = f.name, Value = Local(f.name) }).ToArray() } },
            Methods = new[] { new BirFunctionDeclaration { Name = "invoke", Static = false, Virtual = false, Override = false,
                Params = parameters.Select((t,i) => new BirParameter { Name = P(i), Type = t }).ToArray(),
                Ret = result, Body = body, BodyTerminates = true, SourceClrBoundary = native } },
        };
        file.Types = file.Types.OrEmpty().Append(type).ToArray();
    }

    // The environment contains the caller's conversions, oriented according to
    // the native variance. Building captures descends only through the argument
    // expression. A reference to a cached template never unfolds Invoke again.
    IBirExpression Mapper(TypeNode from, TypeNode to, Dictionary<(TypeNode, TypeNode), Func<IBirExpression>> maps)
    {
        if (maps.TryGetValue((from, to), out var mapped)) return mapped();
        var slot = FuncOf(from, to);
        if (from is TypeNode.Fqn { Args: { } aa } a && to is TypeNode.Fqn { Args: { } bb } b
            && aa.Length == bb.Length && refs.IsClrDelegate(a) && ClrMemberResolution.ResolveOwnerType(a, refs) is { } def
            && def == ClrMemberResolution.ResolveOwnerType(b, refs))
        {
            var formals = def.GetGenericArguments();
            var different = aa.Select((t, i) => t != bb[i]).ToArray();
            var key = (def, string.Concat(different.Select(v => v ? '1' : '0')));
            if (!templates.TryGetValue(key, out var name))
            {
                templates[key] = name = Fresh();
                Define(name, def, different);
            }
            var actual = new List<TypeNode>(aa);
            var captures = new List<IBirExpression>();
            for (int i = 0; i < aa.Length; i++) if (different[i])
            {
                actual.Add(bb[i]);
                captures.Add(Contra(formals[i]) ? Mapper(bb[i], aa[i], maps) : Mapper(aa[i], bb[i], maps));
            }
            return Close(name, actual.ToArray(), slot, captures.ToArray());
        }
        if (scalar == null)
        {
            scalar = Fresh();
            Class(scalar, [new() { Name = "From" }, new() { Name = "To" }], [], [Tv(0)], Tv(1),
                [Return(Cast(Tv(1), Cast(new TypeNode.Fqn("System.Object"), Local(P(0)))))]);
        }
        return Close(scalar, [from, to], slot);
    }
    static bool Contra(Type parameter) => (parameter.GenericParameterAttributes & GenericParameterAttributes.VarianceMask)
        == GenericParameterAttributes.Contravariant;

    void Define(string name, Type def, bool[] different)
    {
        var ps = def.GetGenericArguments();
        var source = Enumerable.Range(0, ps.Length).Select(i => (TypeNode)Tv(i)).ToArray();
        var target = source.ToArray();
        var extra = Enumerable.Range(0, ps.Length).Where(i => different[i]).ToArray();
        for (int j = 0; j < extra.Length; j++) target[extra[j]] = Tv(ps.Length + j);
        var frame = ps.Select((p, i) => Parameter(p, source, i)).Concat(extra.Select((i, j) => Parameter(ps[i], target, ps.Length + j))).ToArray();
        // A shared slot can depend on a varying sibling (where Keep : T).
        // It occupies both constructed types, and must satisfy both sets of bounds.
        for (var i = 0; i < ps.Length; i++)
            if (!different[i] && frame[i].Constraints.HasValue)
                frame[i].Constraints = frame[i].Constraints.Value.Concat(
                    Parameter(ps[i], target, i).Constraints.OrEmpty()).Distinct().ToArray();
        BirTypeParameter Parameter(Type p, TypeNode[] arguments, int ordinal)
        {
            var declaration = CapturedGenericInvocation.CloneParameter(ReferenceMetadataReader.GenericParamDeclaration(p, true),
                t => SupertypeGraph.SubstOwnerTvs(t, arguments));
            declaration.Name = "T" + ordinal; declaration.Variance = default; return declaration;
        }
        var a = new TypeNode.Fqn(def.FullName, source); var b = new TypeNode.Fqn(def.FullName, target);
        var all = Enumerable.Range(0, frame.Length).Select(i => (TypeNode)Tv(i)).ToArray();
        var invoker = Fresh();
        var fields = new List<(string name, TypeNode type)>();
        var env = new Dictionary<(TypeNode, TypeNode), Func<IBirExpression>>();
        foreach (int i in extra)
        {
            var (left, right) = Contra(ps[i]) ? (target[i], source[i]) : (source[i], target[i]);
            var field = "c" + i; fields.Add((field, FuncOf(left, right))); env[(left, right)] = () => Field(invoker, field);
        }
        IBirExpression Convert(IBirExpression e, TypeNode left, TypeNode right) =>
            left == right ? e : Apply(Mapper(left, right, env), left, right, e);
        var (ra, pa, _) = Signature(a); var (rb, pb, _) = Signature(b);
        var call = Call(a, Field(invoker, "source"), pa.Select((t, i) => Convert(Local(P(i)), pb[i], t)).ToArray());
        var body = rb is TypeNode.Fqn { Name: "void" or "System.Void" } ? new IBirStatement[] {
            new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(call) }, new BirReturn() } :
            [Return(SignatureValueTypes.Of(rb) is TypeNode.ByRef ? new BirByRefOf { Inner = OptionalField<IBirExpression>.Of(call) }
                : Convert(call,ra,rb))];
        Class(invoker, frame, [("source", a), .. fields], pb, rb, body, native: rb is TypeNode.Array);
        var made = Close(invoker, all, b, [Local(P(0)), .. fields.Select(f => (IBirExpression)Field(name, f.name))]);
        Class(name, frame, fields.ToArray(), [a], b, [Return(new BirConditional { Type = b,
            Cond = new BirBinaryOperation { Op = "==", Lhs = Local(P(0)), Rhs = Null() }, Then = Null(),
            Else = new BirConditional { Type = b, Cond = new BirIsInstance { Type = b,E = Local(P(0)) },
                Then = OptionalField<IBirExpression>.Of(Cast(b,Local(P(0)))), Else = OptionalField<IBirExpression>.Of(made) } })]);
    }
}
