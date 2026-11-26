using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    static string ConstructionSlotId(string id) => DeclarationIdentityBinding.PhysicalOnlyId(id, "constructed-interface-slot");

    static void AddConstructedDispatchView(Owner owner, IBirTypeDeclaration definition, BirInterfaceDefinition erased,
        CarrierRealizationContext context)
    {
        if (definition.Kind.OrDefault() != "interface") return;
        var rows = context.Declarations.Slots(owner).Values.Where(row => row.EmittedSlot != null && row.ReferenceContract == null &&
            row.Declaration.DeclarationId.HasValue && row.Declaration.TypeParams.OrEmpty().Any(p => p.Constraints.OrEmpty().Any(ContainsOwnerTv))).ToArray();
        if (rows.Length == 0) return;
        var formals = Enumerable.Range(0, owner.Arity).Select(i => new BirTypeParameter { Name = "T" + i }).ToArray();
        var name = context.Demand.AllocateTypeName(owner.Name + "$selection");
        var open = new TypeNode.Fqn(name, Enumerable.Range(0, formals.Length).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
        var methods = new List<BirFunctionDeclaration>();
        foreach (var row in rows)
        {
            var source = row.Declaration;
            var slot = (BirFunctionDeclaration)row.EmittedSlot.DeepClone();
            var id = source.DeclarationId.Value;
            slot.DeclarationId = ConstructionSlotId(id);
            methods.Add(slot);
            context.NativeProofs.TransferIndependentMethodProofs(source, slot);
            var bridge = row.InterfaceForwarder;
            var selected = (BirMethodImpl)bridge.ClrInterfaceImpls.Value.Single().DeepClone();
            selected.Owner = open;
            context.Declarations.ConstructedDispatch.Add(id, (name, selected.Member.Value, source.Params.OrEmpty().Select(p => p.Type.Value).ToArray()));
            bridge.ClrInterfaceImpls = bridge.ClrInterfaceImpls.Value.Append(selected).ToArray();
            DeclarationSignatureIdentity.RecordSelectedImplementationSlot(bridge, slot);
            context.Bindings.GetSource(selected);
            context.NativeProofs.TransferIndependentMethodProofs(source, selected);
        }
        var view = new BirInterfaceDefinition { Name = name, TypeParams = formals, Kind = "interface", Generated = true,
            Vis = erased.Vis, NestedIn = erased.NestedIn, Methods = methods.ToArray(), Attrs = new[] { RoundtripMetadata.ConstructedDispatchMarker() },
            Interfaces = Array.Empty<TypeNode>(), Ctors = Array.Empty<BirConstructor>(), Fields = Array.Empty<BirFieldDeclaration>(),
            Properties = Array.Empty<BirPropertyDeclaration>() };
        context.AttachType(context.Root(owner), view);
        definition.Interfaces = definition.Interfaces.OrEmpty().Append(open).ToArray();
    }

    // A foreign interface can combine several applications of a Kotlin interface. Its one
    // erased receiver cannot distinguish those applications. Prefer the selected exact
    // interface when implemented; an unchecked Kotlin view still uses the erased entry.
    static Func<BirGraphValue, BirTemporaryNames, BirGraphValue> PrepareConstructedDispatch(BirGraphValue node, SourceRewriteState state)
    {
        if (node is not BirCallInstance call || call.Super.OrDefault() || call.MemberRef.HasValue ||
            call._exactExistentialBridgeOwnerCall.OrDefault() || !call.Recv.HasValue || !call.DeclarationId.HasValue ||
            call.OwnerType.OrDefault() is not TypeNode.Fqn { Args.Length: > 0 } owner ||
            owner.Args.Any(ContainsExistentialProjection) ||
            !(state.Definition(owner.Name)?.Kind.OrDefault() == "interface" || state.References.IsInterfaceType(owner)) ||
            !state.Projection.TryExistentialCarrier(owner.Name, out var carrier)) return null;
        var id = call.DeclarationId.Value;
        if (!state.Declarations.ConstructedDispatch.TryGetValue(id, out var selectedSlot))
        {
            if (!state.References.TryDeclarationSignatureSlot(ConstructionSlotId(id), out var imported)) return null;
            selectedSlot = (imported.Owner, imported.Name, imported.Parameters);
        }
        var target = new TypeNode.Fqn(selectedSlot.Owner, owner.Args);
        var result = call.Sty.OrDefault() ?? call.Ret.OrDefault();
        var args = call.Args.OrEmpty();
        var formals = call.Sig.OrDefault() ?? call.ShapeTypes.OrDefault() ?? call.ResolvedMemberParams.OrDefault() ?? selectedSlot.Parameters;
        if (formals.Length != args.Length) throw new InvalidOperationException("Constructed dispatch has no matching invocation parameter frame");
        var types = formals.Select(p => SignatureValueTypes.Of(SubstituteDeclarationTypeArguments(p, owner.Args, call.TypeArgs.OrEmpty()))).ToArray();
        if (result == null) return null;
        var receiver = call.Recv.Value;
        return (fallback, names) => {
            if (names == null || call.OwnerType.OrDefault() is not TypeNode.Fqn erased || erased.Name != carrier) return fallback;
            var statements = new List<IBirStatement>();
            BirLocal Save(IBirExpression input, TypeNode type)
            {
                var name = names.Next("dotkt$dispatch");
                statements.Add(new BirVariable { Name = name, Type = type, Init = OptionalField<IBirExpression>.Of(input) });
                return new BirLocal { Name = name, Sty = type };
            }
            var recv = Save(receiver, erased);
            // Capture a managed location, not its current value. Other operands
            // convert into their selected parameter type just as at the call site.
            var locals = args.Select((arg, i) => Save(types[i] is TypeNode.ByRef && arg is not BirByRefOf
                ? new BirByRefOf { Inner = OptionalField<IBirExpression>.Of(arg) } : arg, types[i])).ToArray();
            // Both views expose the same erased value ABI. Preserve the call's
            // existing result conversion around the choice, not inside one arm.
            var exact = (BirCallInstance)(call with { Recv = default, Args = default }).DeepClone();
            call.Recv = recv;
            call.Args = locals.Cast<IBirExpression>().ToArray();
            exact.OwnerType = target;
            exact.Method = selectedSlot.Member;
            exact.DeclarationId = ConstructionSlotId(id);
            exact.Recv = new BirCast { Type = target, E = recv with { }, Sty = target, _exactBridgeCast = true, _physicalDeclarationSignature = true };
            exact.Args = locals.Select(a => (IBirExpression)(a with { })).ToArray();
            exact._exactExistentialBridgeOwnerCall = true;
            state.SelectedMethodArgumentSlots[exact] = (id, ConstructionSlotId(id));
            if (state.MethodWitnesses.TryGetValue(call, out var witnesses)) state.MethodWitnesses[exact] = witnesses;
            var choice = new BirConditional { Type = call.Ret.OrDefault() ?? result,
                Cond = new BirIsInstance { Type = target, E = recv with { }, ExactDispatchOwner = true },
                Then = exact, Else = call };
            BirGraphValue Select(BirGraphValue wrapped)
            {
                if (ReferenceEquals(wrapped, call)) return choice;
                wrapped.VisitChildSlots(s => { if (s.Value != null) s.Replace(Select(s.Value)); });
                return wrapped;
            }
            return new BirValueBlock { Type = result, Stmts = statements.ToArray(),
                Result = OptionalField<IBirExpression>.Of((IBirExpression)Select(fallback)) };
        };
    }
}
