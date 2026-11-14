using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A native declaration for the direct reflection selector test, not a Kotlin event implementation policy.
abstract class ClrEventImplementationNativeBase<T>
{
    public abstract event Action<T> Changed;
}

static partial class ClrEventImplBinding
{
    internal static void SelfTestTypedImplementations()
    {
        var refs = ReferenceMetadataIndex.Build(new[] {
            typeof(object).Assembly.Location, typeof(System.ComponentModel.INotifyPropertyChanged).Assembly.Location,
            typeof(ClrEventImplementationNativeBase<>).Assembly.Location,
        });
        try
        {
            var text = new TypeNode.Fqn("kotlin.String");
            var variable = new TypeNode.Tv("type", 0);
            var handler = new TypeNode.Fn(false, new TypeNode.Fqn("kotlin.Unit"), new TypeNode[] { variable });
            BirFunctionDeclaration Accessor(string kind, TypeNode sourceHandler) => new() {
                Name = kind + "_Changed", Params = kind == "raise" ? Array.Empty<BirParameter>()
                    : new[] { new BirParameter { Name = "value", Type = sourceHandler, KotlinType = sourceHandler } },
                Body = new IBirStatement[] { new BirClrEventAccessor { Kind = kind, Event = "Changed" } },
                Overrides = Array.Empty<BirOverride>(),
            };
            BirClassDefinition Owner(string name, TypeNode sourceHandler, string modality = "open") => new() {
                Name = name, Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
                ClrEvents = new[] { new BirClrEventBacking { K = "clrEventBacking", Name = "Changed",
                    HandlerType = sourceHandler, Modality = modality, PropertyAssociation = "property" } },
                Methods = new[] { Accessor("add", sourceHandler), Accessor("remove", sourceHandler), Accessor("raise", sourceHandler),
                    new BirFunctionDeclaration { Name = "getChanged", PropertyAssociation = "property", PropertyAccessor = "get", Virtual = true } },
            };
            var sourceBase = Owner("LocalBase", handler);
            var baseAdd = sourceBase.Methods.Value[0];
            baseAdd.Params.Value[0].FunctionSignatureType = handler;
            var unrelated = new BirFunctionDeclaration { Name = "add_Changed", Params = new[] { new BirParameter { Type = text } },
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = new BirThis() } } };
            sourceBase.Methods = new[] { unrelated }.Concat(sourceBase.Methods.Value).ToArray();
            var baseFile = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { sourceBase } };
            var localDerived = Owner("LocalDerived", new TypeNode.Fqn("kotlin.Nothing"), "final");
            localDerived.ClrEvents.Value[0].ClassOverrideOwner = new TypeNode.Fqn("LocalBase", new TypeNode[] { text });
            var derivedAdd = localDerived.Methods.Value[0];
            var existingParameter = derivedAdd.Params.Value[0];
            var existingField = new BirFieldDeclaration { Name = "existing", Type = text };
            localDerived.Fields = new[] { existingField };
            var nativeDerived = Owner("NativeDerived", handler, "final");
            nativeDerived.ClrEvents.Value[0].ClassOverrideOwner = new TypeNode.Fqn(
                nameof(ClrEventImplementationNativeBase<object>), new TypeNode[] { variable });
            var nativeAdd = nativeDerived.Methods.Value[0];
            var staticOwner = Owner("Static", new TypeNode.Nullable(new TypeNode.Fn(false,
                new TypeNode.Fqn("kotlin.Unit"), Array.Empty<TypeNode>())), "final");
            staticOwner.ClrEvents.Value[0].Static = true;
            foreach (var method in staticOwner.Methods.Value.Take(3)) method.Static = true;
            var interfaceOwner = new TypeNode.Fqn("System.ComponentModel.INotifyPropertyChanged");
            var forwardAdd = Accessor("add", handler);
            var forwardRemove = Accessor("remove", handler);
            foreach (var method in new[] { forwardAdd, forwardRemove }) {
                method.Name = method.Name.Value.Replace("Changed", "PropertyChanged");
                method.Overrides = new[] { new BirOverride { Owner = interfaceOwner, Member = "PropertyChanged" } };
            }
            var receiver = new BirLocal { Name = "receiver", Sty = interfaceOwner };
            var forwarding = new BirClassDefinition { Name = "Forwarding", Kind = "class",
                ClrEventForwarders = new[] { new BirClrEventForwarder { Name = "PropertyChanged", OwnerType = interfaceOwner, Recv = receiver } },
                Methods = new[] { forwardAdd, forwardRemove },
            };
            var owners = new IBirTypeDeclaration[] { localDerived, nativeDerived, staticOwner, forwarding };
            var file = new BirFileDeclaration { Types = owners };
            var locals = new LocalEvents();
            locals.CaptureSource(baseFile);
            locals.CaptureSource(file);
            // Binding a derived event must use the captured recipe even if MemberStrip detached its source add.
            sourceBase.Methods = new[] { unrelated };
            handler.Params[0] = new TypeNode.Fqn("ChangedAfterCapture");
            if (!ReferenceEquals(BindImplementations(file, refs, locals), file)
                || !ReferenceEquals(file.Types.Value, owners)
                || !ReferenceEquals(derivedAdd.Params.Value[0], existingParameter)
                || !ReferenceEquals(localDerived.Fields.Value[0], existingField))
                throw new InvalidOperationException("Typed event binding replaced its owning declarations or existing storage");
            var declaration = localDerived.ClrEvents.Value.Single();
            if (declaration.K.Value != "clrEventDecl"
                || declaration.DelegateType.Value is not TypeNode.Fqn { Name: "System.Action", Args: [TypeNode.Fqn { Name: "kotlin.String" }] }
                || !derivedAdd.Override.Value || !derivedAdd.Virtual.Value || !derivedAdd.ClrFinal.Value
                || derivedAdd.Overrides.IsPresent || !derivedAdd.SpecialName.Value
                || existingParameter.KotlinType.Value is not TypeNode.Fqn { Name: "kotlin.Nothing" }
                || !TypeNodeDataEquality.Equal(existingParameter.FunctionSignatureType.Value,
                    FunctionSignatureIdentity.DeclarationType(new TypeNode.Fn(false,
                        new TypeNode.Fqn("kotlin.Unit"), new TypeNode[] { text })))
                || localDerived.Methods.Value[2].Params.Value.Single().Type.Value != text
                || !localDerived.Methods.Value[2].Virtual.Value
                || !localDerived.Methods.Value[3].ClrFinal.Value
                || declaration.EventRaiseDeclarationId.Value != localDerived.Methods.Value[2].DeclarationId.Value
                || derivedAdd.Body.Value.Single() is not BirEventAccessorBody { Kind.Value: "add", Name.Value: "Changed$delegate" }
                || localDerived.Fields.Value[1].Vis.Value != "private")
                throw new InvalidOperationException("Typed local event binding lost source recipe, exact slot, modality or raise identity");
            var nativeSlot = nativeAdd.ClrBaseImpls.Value.Single();
            if (nativeSlot.Member.Value != "add_Changed" || nativeSlot.Arity.Value != 0
                || nativeSlot.Owner.Value is not TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 0 }] }
                || !nativeAdd.Override.Value || !nativeAdd.ClrFinal.Value
                || nativeAdd.Params.Value[0].FunctionSignatureType.IsPresent
                || !TypeNodeDataEquality.Equal(nativeAdd.Params.Value[0].Type.Value, nativeSlot.Params.Value.Single()))
                throw new InvalidOperationException("Typed native event override lost its open frame or exact native signature");
            if (staticOwner.ClrEvents.Value[0].DelegateType.Value is not TypeNode.Fqn { Name: "System.Action", Args: null }
                || !staticOwner.Fields.Value[0].Static.Value || staticOwner.Methods.Value.Take(3).Any(method =>
                    method.Override.Value || method.Virtual.Value || method.ClrFinal.Value
                    || method.Body.Value[0] is not BirEventAccessorBody { Static.Value: true }))
                throw new InvalidOperationException("Typed static nullable handler binding changed delegate shape or static slot flags");
            if (forwarding.ClrEventForwarders.IsPresent || forwarding.Fields.Value.Length != 0
                || forwarding.ClrEvents.Value.Single().EventRaiseDeclarationId.IsPresent
                || forwardAdd.Body.Value.Single() is not BirExpressionStatement { Expr.Value: BirEventAdd add }
                || !add.HandlerExact.Value || add.Static.Value || !forwardAdd.SpecialName.Value
                || ReferenceEquals(add.Recv.Value, receiver)
                || forwardRemove.Body.Value.Single() is not BirExpressionStatement { Expr.Value: BirEventRemove remove }
                || ReferenceEquals(add.Recv.Value, remove.Recv.Value) || !remove.HandlerExact.Value)
                throw new InvalidOperationException("Typed delegated event forwarding lost exact handlers or independent receiver occurrences");
            if (!locals.TryGetSource(new TypeNode.Fqn("LocalBase"), "Changed", out var recipe)
                || recipe.Add.Params.Value[0].Type.Value is not TypeNode.Fn { Params: [TypeNode.Tv] })
                throw new InvalidOperationException("Typed event binding recaptured a changed or detached source accessor");
        }
        finally { refs.DisposeNet(); }
        Console.WriteLine("[typed event implementations] self-test OK (continuing owners, local/native frames, static/final slots, exact forwarding, pre-strip authority)");
    }
}
