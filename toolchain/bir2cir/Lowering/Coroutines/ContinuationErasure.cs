using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// The cold coroutine protocol uses monomorphic Result/Continuation value surfaces where the earlier
// existential projection has not already supplied its carrier. CLR variance cannot relate value-type
// instantiations, so ordinary values, construction sites and inherited protocol edges share that ABI.
// An exact lexical receiver is different: its own member operand must retain the actual declaring frame,
// not name G<object> while loading this of G<T>. Source signatures remain in the existing metadata channel.
// Runs in ref/runtime/app before BirTypeLowering; ilemit receives only the resulting physical contracts.
static partial class ContinuationErasure
{
    const string Cont = "kotlin.coroutines.Continuation";
    const string ResultFqn = "kotlin.Result";
    static IReadOnlySet<string> _continuationTypeNames = new HashSet<string>(StringComparer.Ordinal) { Cont };
    static IReadOnlySet<string> _resultTypeNames = new HashSet<string>(StringComparer.Ordinal) { ResultFqn };
    static IReadOnlyDictionary<string, string> _physicalTypeNames = new Dictionary<string, string>();

    // Supertype edges are source declaration facts too. Freeze them before nullable-frame allocation and
    // other representation passes; recording only member slots leaves Continuation<T>'s inherited contract
    // paired with a physical Continuation<object> edge when a DLL is imported again.
    public static BirFileDeclaration[] PreserveSourceSupertypes(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        SetTypeNames(refs.PhysicalTypeNames);
        return roots.Select(root => (BirFileDeclaration)root.Rewrite(CaptureSupertypes)).ToArray();
    }

    static void SetTypeNames(IReadOnlyDictionary<string, string> physicalTypeNames)
    {
        _physicalTypeNames = physicalTypeNames;
        var continuationNames = new HashSet<string>(StringComparer.Ordinal) { Cont };
        if (physicalTypeNames.TryGetValue(Cont, out var physicalContinuation))
            continuationNames.Add(physicalContinuation);
        _continuationTypeNames = continuationNames;
        var resultNames = new HashSet<string>(StringComparer.Ordinal) { ResultFqn };
        if (physicalTypeNames.TryGetValue(ResultFqn, out var physicalResult))
            resultNames.Add(physicalResult);
        _resultTypeNames = resultNames;
    }

    readonly record struct ResultReceiverPlan(bool Arguments, bool Return, bool Stamp);
    static ResultReceiverPlan PlanResultReceiver(bool inResumeWith, bool ordinaryCall, int arity,
        bool resultReceiver, string method, TypeNode result, bool stamp)
    {
        // Result<T>'s erased receiver fixes its generic application at object.
        // T-returning Unit/void hints must also become object (and update an
        // existing stamp), or an exprStmt leaves that pushed value unpopped.
        // exceptionOrNull is not T-returning and retains its Throwable result.
        var arguments = inResumeWith && ordinaryCall && arity > 0 && resultReceiver;
        var promote = arguments && method is "getOrThrow" or "getOrNull" or "getOrDefault" or "getOrElse"
            && result is TypeNode.Fqn { Args: null, Name: "void" or "kotlin.Unit" };
        return new(arguments, promote, promote && stamp);
    }
    static bool IsResultFactory(string owner, string method, bool arguments)
        => owner == ResultFqn && method is "success" or "failure" && arguments;
    static bool IsLexicalTypeKind(string kind) => kind is "class" or "interface" or "struct" or "enum" or "delegate";

    static BirGraphValue CaptureSupertypes(BirGraphValue node)
    {
        bool Moves(TypeNode edge) => edge != null && !EraseType(edge).Equals(edge);
        OptionalField<SourceTypeFacts> Capture(OptionalField<TypeNode> baseType, OptionalField<TypeNode[]> interfaces,
            OptionalField<BirTypeParameter[]> parameters, OptionalField<SourceTypeFacts> previous)
        {
            var original = previous.OrDefault() ?? SourceTypeFacts.Empty;
            var bounds = parameters.OrEmpty().Select((parameter, index) => (parameter, index))
                .Where(entry => entry.parameter.Constraints.OrEmpty().Any(Moves))
                .ToDictionary(entry => entry.index, entry => entry.parameter.Constraints.Value);
            var merged = original.Merge(Moves(baseType.OrDefault()) ? baseType.Value : null,
                interfaces.OrEmpty().Where(Moves).ToArray(), bounds);
            return ReferenceEquals(original, merged) ? previous : merged;
        }
        return node switch {
            BirClassDefinition owner => owner with {
                KotlinSupertypesPre = Capture(owner.Base, owner.Interfaces, owner.TypeParams, owner.KotlinSupertypesPre),
            },
            BirInterfaceDefinition owner => owner with {
                KotlinSupertypesPre = Capture(owner.Base, owner.Interfaces, owner.TypeParams, owner.KotlinSupertypesPre),
            },
            BirEnumDefinition owner => owner with {
                KotlinSupertypesPre = Capture(owner.Base, owner.Interfaces, owner.TypeParams, owner.KotlinSupertypesPre),
            },
            // An inherited implementation descriptor also owns its declared generic bounds.
            BirInheritedImplementation owner => owner with {
                KotlinSupertypesPre = Capture(default, default, owner.TypeParams, owner.KotlinSupertypesPre),
            },
            _ => node,
        };
    }

    internal static void SelfTest()
    {
        FunctionRewriteTests.Check("continuation erasure", EraseType);
        LexicalOwnerSelfTest();
        SourceOwnerSelfTest();
        SourceProtocolSelfTest();
        var names = new Dictionary<string, string> {
            [Cont] = "kotlin.coroutines.Continuation`1", [ResultFqn] = "kotlin.Result`1",
        };
        var integer = new TypeNode.Fqn("kotlin.Int");
        var any = new TypeNode.Fqn("kotlin.Any");
        foreach (var sourceName in new[] { Cont, ResultFqn })
        foreach (var typeName in new[] { sourceName, names[sourceName] })
        {
            TypeNode Nested(TypeNode argument) => new TypeNode.Fqn("probe.Container", new TypeNode[] { new TypeNode.Fqn(typeName, new[] { argument }) });
            var original = Nested(integer); var expected = Nested(any);
            BirMethodImpl Descriptor() => new() { Owner = original, Params = new[] { original }, Ret = original };
            var staticCall = new BirCallStatic { Owner = new TypeNode.Fqn(Cont) };
            var method = new BirFunctionDeclaration { Name = "member", Params = Array.Empty<BirParameter>(),
                Overrides = new[] { new BirOverride { Owner = original } },
                ClrInterfaceImpls = new[] { Descriptor() }, ClrBaseImpls = new[] { Descriptor() },
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = staticCall } } };
            var input = new BirClassDefinition { Name = Cont, Kind = "class", Base = original,
                Interfaces = new[] { original }, TypeParams = new[] { new BirTypeParameter {
                    Name = "T", Constraints = new TypeNode[] { original, new TypeNode.Fqn("probe.Unchanged") },
                } }, Methods = new[] { method } };
            SetTypeNames(names);
            var captured = (BirClassDefinition)CaptureSupertypes(input);
            var facts = captured.KotlinSupertypesPre.Value;
            var snapshot = SourceTypeFacts.ToDom(facts).GetValue<string>();
            var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { captured } };
            Apply(file, names);
            EqualSourceType(captured.Interfaces.Value[0], expected, typeName + " implemented interface");
            EqualSourceType(method.Overrides.Value[0].Owner.Value, original, typeName + " semantic override owner");
            EqualSourceType(staticCall.Owner.Value, new TypeNode.Fqn(Cont), typeName + " static declaration container");
            foreach (var descriptor in method.ClrInterfaceImpls.Value.Concat(method.ClrBaseImpls.Value))
            {
                EqualSourceType(descriptor.Owner.Value, expected, typeName + " MethodImpl owner");
                EqualSourceType(descriptor.Params.Value[0], expected, typeName + " MethodImpl parameter");
                EqualSourceType(descriptor.Ret.Value, expected, typeName + " MethodImpl return");
            }
            if (!ReferenceEquals(captured.KotlinSupertypesPre.Value, facts)
                || SourceTypeFacts.ToDom(facts).GetValue<string>() != snapshot || input.KotlinSupertypesPre.IsPresent)
                throw new InvalidOperationException("Continuation erasure changed frozen source metadata or original capture ownership");
            var competing = Nested(new TypeNode.Tv("method", 3));
            var later = (BirClassDefinition)CaptureSupertypes(captured with { Base = competing,
                Interfaces = new[] { competing }, TypeParams = new[] { new BirTypeParameter {
                    Name = "T", Constraints = new TypeNode[] { competing, new TypeNode.Fqn("probe.Unchanged") },
                } } });
            if (SourceTypeFacts.ToDom(later.KotlinSupertypesPre.Value).GetValue<string>() != snapshot)
                throw new InvalidOperationException("Continuation source capture replaced earlier source facts");
        }
    }

    static void EqualSourceType(TypeNode actual, TypeNode expected, string role)
    {
        if (!TypeNodeDataEquality.Equal(actual, expected))
            throw new InvalidOperationException("Continuation erasure: " + role);
    }

    static void SourceOwnerSelfTest()
    {
        SetTypeNames(new Dictionary<string, string>());
        var source = new TypeNode.Fqn(Cont, new TypeNode[] { new TypeNode.Tv("type", 2) });
        var sibling = new TypeNode.Fqn("probe.Sibling");
        var bounds = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { source, sibling } } };
        var descriptor = new BirInheritedImplementation { Kind = "function", TypeParams = bounds };
        var method = new BirFunctionDeclaration { Name = "member", TypeParams = bounds,
            InheritedImplementation = descriptor, Body = new IBirStatement[] { new BirExpressionStatement {
                Expr = new BirNewSam { SynthClass = new BirClassDefinition {
                    Kind = "class", Name = "probe.Synthetic", Base = source,
                    InheritedDefaultAccessors = new[] { new BirInheritedDefaultAccessor { Implementation = descriptor } },
                    InheritedDefaultMethods = new[] { new BirInheritedDefaultMethod { Implementation = descriptor } },
                } },
            } } };
        var input = new BirFileDeclaration { Methods = new[] { method }, Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Kind = "class", Name = "probe.Class", Base = source,
                Interfaces = new TypeNode[] { source, sibling }, TypeParams = bounds },
            new BirInterfaceDefinition { Kind = "interface", Name = "probe.Interface",
                Interfaces = new TypeNode[] { source }, TypeParams = bounds },
            new BirEnumDefinition { Kind = "enum", Name = "probe.Enum", Base = source, TypeParams = bounds },
        } };
        var result = (BirFileDeclaration)input.Rewrite(CaptureSupertypes);
        var classOwner = (BirClassDefinition)result.Types.Value[0];
        var interfaceOwner = (BirInterfaceDefinition)result.Types.Value[1];
        var enumOwner = (BirEnumDefinition)result.Types.Value[2];
        var inherited = result.Methods.Value[0].InheritedImplementation.Value;
        var synthetic = ((BirNewSam)((BirExpressionStatement)result.Methods.Value[0].Body.Value[0]).Expr.Value).SynthClass.Value;
        foreach (var facts in new[] { classOwner.KotlinSupertypesPre.Value, interfaceOwner.KotlinSupertypesPre.Value,
            enumOwner.KotlinSupertypesPre.Value, inherited.KotlinSupertypesPre.Value,
            synthetic.InheritedDefaultAccessors.Value[0].Implementation.Value.KotlinSupertypesPre.Value,
            synthetic.InheritedDefaultMethods.Value[0].Implementation.Value.KotlinSupertypesPre.Value })
            if (facts.Bounds[0].Length != 2 || facts.Bounds[0][0] != source || facts.Bounds[0][1] != sibling)
                throw new InvalidOperationException("Continuation source bounds lost a generic frame or unchanged sibling");
        if (classOwner.KotlinSupertypesPre.Value.Base != source
            || classOwner.KotlinSupertypesPre.Value.Interfaces.Single() != source
            || interfaceOwner.KotlinSupertypesPre.Value.Interfaces.Single() != source
            || enumOwner.KotlinSupertypesPre.Value.Base != source
            || inherited.KotlinSupertypesPre.Value.Base != null
            || synthetic.KotlinSupertypesPre.Value.Base != source
            || ((BirClassDefinition)input.Types.Value[0]).KotlinSupertypesPre.IsPresent
            || descriptor.KotlinSupertypesPre.IsPresent
            || classOwner.Base.Value != source || classOwner.Interfaces.Value[1] != sibling)
            throw new InvalidOperationException("Continuation source capture changed declaration ownership or physical types");
        var existing = new SourceTypeFacts(new Dictionary<int, TypeNode[]> { [0] = new TypeNode[] { sibling } });
        var recorded = (BirClassDefinition)CaptureSupertypes(classOwner with { KotlinSupertypesPre = existing });
        if (recorded.KotlinSupertypesPre.Value.Bounds[0].Single() != sibling)
            throw new InvalidOperationException("Continuation source capture replaced earlier source bounds");
        var unchanged = (BirClassDefinition)CaptureSupertypes(new BirClassDefinition {
            Kind = "class", Name = "probe.Plain", Base = sibling,
        });
        if (unchanged.KotlinSupertypesPre.IsPresent)
            throw new InvalidOperationException("Continuation source capture invented an absent source fact");
    }

    static TypeNode.Fqn Head(TypeNode type) => type switch
    {
        TypeNode.Fqn owner => owner,
        TypeNode.Nullable nullable => Head(nullable.Of),
        TypeNode.Oblivious oblivious => Head(oblivious.Of),
        _ => null,
    };

    static bool SameDeclaration(string left, string right) => left == right
        || right != null && _physicalTypeNames.TryGetValue(right, out var physicalRight) && left == physicalRight
        || left != null && _physicalTypeNames.TryGetValue(left, out var physicalLeft) && right == physicalLeft;

    static bool IsExactReceiverTypeSlot(string key, TypeNode type, string lexicalOwner, bool exactOuter, bool self,
        bool receiverThis, bool receiverOuter, TypeNode receiverType)
    {
        if (key == "ownerType")
        {
            var selected = Head(type);
            if (selected != null && receiverThis
                && SameDeclaration(selected.Name, lexicalOwner)) return true;
            // An enclosing receiver's own declaration, not an inherited/interface edge on that receiver.
            if (selected != null && receiverOuter && Head(receiverType) is { } enclosing
                && SameDeclaration(selected.Name, enclosing.Name)
                && (selected.Args ?? Array.Empty<TypeNode>()).SequenceEqual(enclosing.Args ?? Array.Empty<TypeNode>())) return true;
        }
        return key is "type" or "sty" or "memberType" or "ret" or "ownerType"
            && (exactOuter || self && Head(type) is { } selfType && SameDeclaration(selfType.Name, lexicalOwner));
    }

    // Erase Continuation + constructed Result to their monomorphic `<kotlin.Any>` form, recursively through a
    // structured Type. `kotlin.coroutines.Continuation[X]` AND a bare `Continuation` (the star-projection) ->
    // Continuation[kotlin.Any]; a CONSTRUCTED `kotlin.Result[X]` -> Result[kotlin.Any] (a BARE Result — a raw-type
    // value — is left untouched). A leading-substring false match (ContinuationImpl / SafeContinuation / ResultKt) is
    // impossible on a structured Fqn (the Name is an exact FQN, not a substring). Nested args/nullable/array/byRef/fn
    // are recursed so a Continuation/Result buried in a generic arg or delegate signature erases too.
    static readonly TypeNode[] AnyArg = { new TypeNode.Fqn("kotlin.Any") };

    static TypeNode EraseType(TypeNode t)
    {
        switch (t)
        {
            case TypeNode.Fqn f:
                if (_continuationTypeNames.Contains(f.Name)) return new TypeNode.Fqn(f.Name, AnyArg);
                // A trusted inline/default payload can already carry the referenced TypeDef's exact metadata name
                // (`kotlin.Result`1`) while source-authored slots still use `kotlin.Result`. Both identities come from
                // the explicit reference index; recognizing the physical twin here avoids splitting one monomorphic
                // Result value between Result<object> and Result<T> locals without reconstructing a name convention.
                if (_resultTypeNames.Contains(f.Name) && f.Args != null)
                    return new TypeNode.Fqn(f.Name, AnyArg);
                return f.Args == null ? f : new TypeNode.Fqn(f.Name, f.Args.Select(EraseType).ToArray());
            case TypeNode.Nullable n: return new TypeNode.Nullable(EraseType(n.Of));
            case TypeNode.Array a: return new TypeNode.Array(EraseType(a.Elem));
            case TypeNode.ByRef b: return new TypeNode.ByRef(EraseType(b.Of));
            case TypeNode.Fn fn: return new TypeNode.Fn(fn.Suspend, EraseType(fn.Ret),
                fn.Params.Select(EraseType).ToArray(), fn.Recv == null ? null : EraseType(fn.Recv), fn.Clr,
                fn.Ctx?.Select(EraseType).ToArray());
            default: return t;
        }
    }
}
