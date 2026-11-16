using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ContinuationErasure
{
    internal static void Apply(BirFileDeclaration root, ReferenceMetadataIndex refs)
        => Apply(root, refs.PhysicalTypeNames);

    static void Apply(BirFileDeclaration root, IReadOnlyDictionary<string, string> physicalTypeNames)
    {
        SetTypeNames(physicalTypeNames);
        RecordSourceDeclarationSurfaces(root);
        WalkSource(root, false, null);
    }

    // Capture only declaration carriers that own a current type slot. Encoded
    // source facts and executable payloads are not another declaration forest.
    static void RecordSourceDeclarationSurfaces(BirGraphValue node)
    {
        if (node is IBirExecutablePayload) return;
        OptionalField<TypeNode> Capture(OptionalField<TypeNode> slot, OptionalField<TypeNode> previous,
            OptionalField<TypeNode> nullable, bool exact = false)
        {
            // An earlier nullable/representation pass may already have captured
            // Kotlin meaning. Its current slot is then physical input, not a
            // replacement source signature for dll2klib to prefer over that fact.
            if (previous.HasValue || nullable.HasValue || !slot.HasValue || exact) return previous;
            return SourceSurfaceChanges(slot.Value) ? BirGraphValue.CopyType(slot.Value) : previous;
        }
        switch (node)
        {
            case BirFunctionDeclaration method when method.Name.HasValue && method.Params.HasValue:
                method.RetKotlinType = Capture(method.Ret, method.RetKotlinType, method.NullableGenericRet); break;
            case BirParameter parameter:
                parameter.KotlinType = Capture(parameter.Type, parameter.KotlinType, parameter.NullableGeneric, parameter.Outer.OrDefault()); break;
            case BirLambdaParameter parameter:
                parameter.KotlinType = Capture(parameter.Type, parameter.KotlinType, parameter.NullableGeneric, parameter.Outer.OrDefault()); break;
            case BirFieldDeclaration field:
                field.KotlinType = Capture(field.Type, field.KotlinType, field.NullableGeneric, field.Outer.OrDefault()); break;
            case BirPropertyDeclaration property:
                property.KotlinType = Capture(property.Type, property.KotlinType, property.NullableGeneric); break;
            case BirCaptureDescriptor capture:
                capture.KotlinType = Capture(capture.Type, capture.KotlinType, capture.NullableGeneric, capture.Outer.OrDefault()); break;
        }
        node.VisitOwnChildSlots((_, role, slot) => {
            if (!NullableRepresentationViews.Frozen(role)) RecordSourceDeclarationSurfaces(slot.Value);
        });
    }
    static bool SourceSurfaceChanges(TypeNode original)
        => TypeNode.ToJson(original) != TypeNode.ToJson(EraseType(original));

    static bool SourceExactOuter(BirGraphValue node) => node switch {
        BirFieldGet value => value.Outer.OrDefault(), BirFieldSetExpression value => value.Outer.OrDefault(),
        BirSetField value => value.Outer.OrDefault(), BirLocal value => value.Outer.OrDefault(),
        BirThis value => value.Outer.OrDefault(), BirVariable value => value.Outer.OrDefault(),
        BirFieldDeclaration value => value.Outer.OrDefault(), BirParameter value => value.Outer.OrDefault(),
        BirLambdaParameter value => value.Outer.OrDefault(), BirCaptureDescriptor value => value.Outer.OrDefault(),
        BirInlineCapture value => value.Outer.OrDefault(), _ => false,
    };

    internal static void ConsumeExactOuterSource(BirFileDeclaration root)
    {
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            switch (node)
            {
                case BirFieldGet value: value.Outer = default; break;
                case BirFieldSetExpression value: value.Outer = default; break;
                case BirSetField value: value.Outer = default; break;
                case BirLocal value: value.Outer = default; break;
                case BirThis value: value.Outer = default; break;
                case BirVariable value: value.Outer = default; break;
                case BirFieldDeclaration value: value.Outer = default; break;
                case BirParameter value: value.Outer = default; break;
                case BirLambdaParameter value: value.Outer = default; break;
                case BirCaptureDescriptor value: value.Outer = default; break;
                case BirInlineCapture value: value.Outer = default; break;
            }
            node.VisitOwnChildSlots((_, role, slot) => { if (!NullableRepresentationViews.Frozen(role)) Walk(slot.Value); });
        }
        Walk(root);
    }

    // The existing resume protocol recognizes named declarations, including
    // executable initializers/defaults on such declarations, not call names.
    // Frozen metadata records are never visited as executable declarations.
    static bool IsSourceResumeDeclaration(BirGraphValue node) => (node switch {
        BirFunctionDeclaration value => value.Name.OrDefault(),
        BirFieldDeclaration value => value.Name.OrDefault(),
        BirPropertyDeclaration value => value.Name.OrDefault(),
        BirParameter value => value.Name.OrDefault(),
        BirLambdaParameter value => value.Name.OrDefault(),
        BirCaptureDescriptor value => value.Name.OrDefault(),
        IBirTypeDeclaration value => value.Name.OrDefault(),
        _ => null,
    }) == "resumeWith";

    static void WalkSource(BirGraphValue node, bool inResumeWith, string lexicalOwner)
    {
        if (node is IBirExecutablePayload) return;
        if (node is IBirTypeDeclaration declaration && IsLexicalTypeKind(declaration.Kind.OrDefault()))
            lexicalOwner = declaration.Name.OrDefault();
        var here = inResumeWith || IsSourceResumeDeclaration(node);
        BirGraphValue receiver = null;
        node.VisitOwnChildSlots((field, _, slot) => { if (field == "recv") receiver = slot.Value; });
        // Compute the exact-owner decision before visiting/retyping descendants.
        // An enclosing receiver retains its own frame, never an ancestor edge.
        var exactKeys = new HashSet<string>(StringComparer.Ordinal);
        node.VisitOwnTypeFields((field, type) => {
            if (IsExactReceiverTypeSlot(field, type, lexicalOwner, SourceExactOuter(node), node is BirThis,
                receiver is BirThis, SourceExactOuter(receiver), SourceExactOuter(receiver) ? BirNodeType.Of(receiver as BirGraphNode) : null)) exactKeys.Add(field);
        });
        EraseSourceResultProtocol(node, here);
        node.RewriteOwnFieldsInPlace((field, role, type) => {
            // Static containers and semantic override owners are declarations;
            // MethodImpl owners instead name constructed physical interfaces.
            if (NullableRepresentationViews.Frozen(role) || field == "name" || field == "owner" && node is not BirMethodImpl
                || exactKeys.Contains(field)) return type;
            // EraseType is also used for scalar comparison and can return its
            // shared AnyArg template. Owning type slots get independent vectors.
            return BirGraphValue.CopyType(EraseType(type));
        }, (_, role, slot) => {
            if (!NullableRepresentationViews.Frozen(role)) WalkSource(slot.Value, here, lexicalOwner);
        });
    }

    static void EraseSourceResultProtocol(BirGraphValue node, bool inResumeWith)
    {
        void RewriteReceiver(string method, OptionalField<TypeNode[]> arguments, IBirExpression[] values,
            OptionalField<TypeNode> result, OptionalField<TypeNode> stamp,
            Action<TypeNode[]> setArguments, Action<TypeNode> setResult, Action<TypeNode> setStamp)
        {
            var plan = PlanResultReceiver(inResumeWith, true, arguments.OrEmpty().Length,
                values.FirstOrDefault() is BirLocal { Name.HasValue: true } local && local.Name.Value == "result",
                method, result.OrDefault(), stamp.HasValue);
            if (plan.Arguments) setArguments(ErasedSourceArguments(arguments.Value.Length));
            if (plan.Return) setResult(new TypeNode.Fqn("kotlin.Any"));
            if (plan.Stamp) setStamp(new TypeNode.Fqn("kotlin.Any"));
        }
        switch (node)
        {
            case BirCallStatic call:
                RewriteReceiver(call.Method.OrDefault(), call.TypeArgs, call.Args.OrEmpty(), call.Ret, call.Sty,
                    types => call.TypeArgs = types, type => call.Ret = type, type => call.Sty = type);
                if (IsResultFactory((call.Owner.OrDefault() as TypeNode.Fqn)?.Name, call.Method.OrDefault(), call.TypeArgs.HasValue))
                    call.TypeArgs = ErasedSourceArguments(call.TypeArgs.Value.Length);
                break;
            case BirCallInstance call:
                RewriteReceiver(call.Method.OrDefault(), call.TypeArgs, call.Args.OrEmpty(), call.Ret, call.Sty,
                    types => call.TypeArgs = types, type => call.Ret = type, type => call.Sty = type);
                break;
        }
    }
    static TypeNode[] ErasedSourceArguments(int count)
        => Enumerable.Range(0, count).Select(_ => (TypeNode)new TypeNode.Fqn("kotlin.Any")).ToArray();
}
