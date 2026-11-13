using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Source declarations keep their occurrence throughout demand, materialization,
// and prepared-default restoration. These views select authored fields only;
// the representation rules remain in the shared frame/type policy.
static class NullableRepresentationViews
{
    internal readonly record struct ReturnSnapshot(TypeNode Return, TypeNode Stamp, bool DeclarationReturn);
    internal static ReturnSnapshot ReturnFacts(BirGraphValue node)
    {
        TypeNode result = null, stamp = null;
        node.RewriteRepresentationInPlace((ownership, role, type) => {
            if (role == BirRepresentationRole.Return) result = type;
            if (role == BirRepresentationRole.ResultStamp) stamp = type;
            return type;
        }, (_, _, _) => { });
        return new(result, stamp, NullableRepresentationTypes.IsDeclarationReturn((node as BirGraphNode)?.Tag, result, stamp));
    }
    internal static bool Frozen(BirGenericFrameRole role) => role is BirGenericFrameRole.FrozenSourceFact or BirGenericFrameRole.StorageSourceType;

    internal sealed class Owner(BirGraphValue node)
    {
        internal BirGraphValue Node => node;
        internal string Name => node switch {
            BirFileDeclaration value => value.FileClass.OrDefault(),
            BirClassDefinition value => value.Name.OrDefault(),
            BirInterfaceDefinition value => value.Name.OrDefault(),
            BirEnumDefinition value => value.Name.OrDefault(),
            BirRefTypeDeclaration value => value.Name.OrDefault(),
            _ => throw Unexpected(node),
        };
        internal string Kind => node switch {
            BirClassDefinition value => value.Kind.OrDefault(),
            BirInterfaceDefinition value => value.Kind.OrDefault(),
            BirEnumDefinition value => value.Kind.OrDefault(),
            _ => null,
        };
        internal bool IsRefCell => node is BirRefTypeDeclaration;
        internal string SemanticOwner => (node as BirClassDefinition)?.SemanticOwner.OrDefault();
        internal int CaptureCount => (node as BirClassDefinition)?.OuterTypeParamCount.OrDefault() ?? 0;
        internal int CaptureOffset => (node as BirClassDefinition)?.OuterTypeParamOffset.OrDefault() ?? 0;
        internal OptionalField<BirTypeParameter[]> Parameters { get => TypeParameters(node); set => SetTypeParameters(node, value); }
        internal TypeNode Base => node switch {
            BirClassDefinition value => value.Base.OrDefault(), BirInterfaceDefinition value => value.Base.OrDefault(),
            BirEnumDefinition value => value.Base.OrDefault(), _ => null,
        };
        internal TypeNode[] Interfaces => node switch {
            BirClassDefinition value => value.Interfaces.OrEmpty(), BirInterfaceDefinition value => value.Interfaces.OrEmpty(),
            BirEnumDefinition value => value.Interfaces.OrEmpty(), _ => Array.Empty<TypeNode>(),
        };
        internal BirFieldDeclaration[] Fields => node switch {
            BirFileDeclaration value => value.Fields.OrEmpty(), BirClassDefinition value => value.Fields.OrEmpty(),
            BirInterfaceDefinition value => value.Fields.OrEmpty(), BirEnumDefinition value => value.Fields.OrEmpty(),
            _ => Array.Empty<BirFieldDeclaration>(),
        };
        internal BirPropertyDeclaration[] Properties => node switch {
            BirFileDeclaration value => value.Properties.OrEmpty(), BirClassDefinition value => value.Properties.OrEmpty(),
            BirInterfaceDefinition value => value.Properties.OrEmpty(), BirEnumDefinition value => value.Properties.OrEmpty(),
            _ => Array.Empty<BirPropertyDeclaration>(),
        };
        internal BirConstructor[] Constructors => node switch {
            BirClassDefinition value => value.Ctors.OrEmpty(), BirInterfaceDefinition value => value.Ctors.OrEmpty(),
            BirEnumDefinition value => value.Ctors.OrEmpty(), _ => Array.Empty<BirConstructor>(),
        };
        internal OptionalField<BirFunctionDeclaration[]> Methods {
            get => node switch {
                BirFileDeclaration value => value.Methods, BirClassDefinition value => value.Methods,
                BirInterfaceDefinition value => value.Methods, BirEnumDefinition value => value.Methods, _ => default,
            };
            set {
                switch (node) {
                    case BirFileDeclaration target: target.Methods = value; break;
                    case BirClassDefinition target: target.Methods = value; break;
                    case BirInterfaceDefinition target: target.Methods = value; break;
                    case BirEnumDefinition target: target.Methods = value; break;
                    default: throw Unexpected(node);
                }
            }
        }
        internal IEnumerable<Method> Inherited => node is BirClassDefinition type
            ? type.InheritedDefaultMethods.OrEmpty().Cast<BirGraphValue>()
                .Concat(type.InheritedDefaultAccessors.OrEmpty()).Concat(type.InheritedClassMethods.OrEmpty())
                .Select(value => new Method(value)) : Enumerable.Empty<Method>();
        internal SourceTypeFacts Facts { get => TypeFacts(node); set => SetTypeFacts(node, value); }
    }

    internal sealed class Method(BirGraphValue node)
    {
        internal BirGraphValue Node => node;
        internal BirFunctionDeclaration Function => node as BirFunctionDeclaration;
        internal BirInheritedImplementation Implementation => node switch {
            BirInheritedDefaultMethod value => value.Implementation.OrDefault(),
            BirInheritedDefaultAccessor value => value.Implementation.OrDefault(),
            BirInheritedClassMethod value => value.InheritedImplementation.OrDefault(), _ => null,
        };
        internal bool Inherited => node is not BirFunctionDeclaration;
        internal string Name => Function?.Name.OrDefault();
        internal string Id => Function?.DeclarationId.OrDefault();
        internal BirParameter[] ValueParameters => node switch {
            BirFunctionDeclaration value => value.Params.OrEmpty(),
            BirInheritedClassMethod value => value.Params.OrEmpty(), _ => Array.Empty<BirParameter>(),
        };
        internal TypeNode[] SignatureParameters => node switch {
            BirFunctionDeclaration value => value.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()).ToArray(),
            BirInheritedDefaultMethod value => value.Params.OrEmpty(),
            BirInheritedDefaultAccessor value => value.Params.OrEmpty(),
            BirInheritedClassMethod value => value.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()).ToArray(), _ => throw Unexpected(node),
        };
        internal TypeNode Return {
            get => node switch {
                BirFunctionDeclaration value => value.Ret.OrDefault(), BirInheritedDefaultMethod value => value.Ret.OrDefault(),
                BirInheritedDefaultAccessor value => value.Ret.OrDefault(), BirInheritedClassMethod value => value.Ret.OrDefault(),
                _ => throw Unexpected(node),
            };
            set {
                switch (node) {
                    case BirFunctionDeclaration target: target.Ret = value; break;
                    case BirInheritedDefaultMethod target: target.Ret = value; break;
                    case BirInheritedDefaultAccessor target: target.Ret = value; break;
                    case BirInheritedClassMethod target: target.Ret = value; break;
                    default: throw Unexpected(node);
                }
            }
        }
        internal TypeNode SourceReturn => node switch {
            BirFunctionDeclaration value => value.RetKotlinType.OrDefault(),
            BirInheritedClassMethod value => value.RetKotlinType.OrDefault(), _ => null,
        };
        internal BirOverride[] Overrides => node switch {
            BirFunctionDeclaration value => value.Overrides.OrEmpty(), BirInheritedDefaultMethod value => value.Overrides.OrEmpty(),
            BirInheritedDefaultAccessor value => value.Overrides.OrEmpty(), BirInheritedClassMethod value => value.Overrides.OrEmpty(),
            _ => throw Unexpected(node),
        };
        internal OptionalField<BirTypeParameter[]> Parameters { get => TypeParameters(Implementation ?? node); set => SetTypeParameters(Implementation ?? node, value); }
        internal IBirStatement[] Body => Function?.Body.OrDefault();
        internal bool CanExtendBodyFrame => !Inherited && !Function.Virtual.OrDefault()
            && !Function.Override.OrDefault() && !Function.Abstract.OrDefault() && Overrides.Length == 0;
        internal bool OwnsValueSlots(GenericRepresentationPolicy policy) => policy?.OwnsMethodValueSlots(
            Function?.Attrs.OrEmpty().Any(attribute => attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrIntrinsic" }) == true,
            Overrides.Select(edge => edge.Owner.OrDefault())) == true;
        internal void SetReturnFact(TypeNode type)
        {
            switch (node) {
                case BirFunctionDeclaration target: if (!target.NullableGenericRet.HasValue) target.NullableGenericRet = type; break;
                case BirInheritedDefaultMethod target: if (!target.NullableGenericRet.HasValue) target.NullableGenericRet = type; break;
                case BirInheritedDefaultAccessor target: if (!target.NullableGenericRet.HasValue) target.NullableGenericRet = type; break;
                case BirInheritedClassMethod target: if (!target.NullableGenericRet.HasValue) target.NullableGenericRet = type; break;
                default: throw Unexpected(node);
            }
        }
    }

    internal static IEnumerable<Owner> Owners(IReadOnlyList<BirFileDeclaration> roots)
    {
        foreach (var root in roots) {
            yield return new Owner(root);
            foreach (var type in root.Types.OrEmpty()) yield return new Owner((BirGraphValue)type);
            foreach (var cell in root.RefTypes.OrEmpty()) yield return new Owner(cell);
        }
    }

    internal static OptionalField<BirTypeParameter[]> TypeParameters(BirGraphValue node) => node switch {
        BirFileDeclaration => default, BirClassDefinition value => value.TypeParams,
        BirInterfaceDefinition value => value.TypeParams, BirEnumDefinition value => value.TypeParams,
        BirRefTypeDeclaration value => value.TypeParams, BirFunctionDeclaration value => value.TypeParams,
        BirInheritedImplementation value => value.TypeParams, BirClosureDefinition value => value.TypeParams,
        BirNewSuspendLambda value => value.TypeParams, _ => throw Unexpected(node),
    };
    internal static void SetTypeParameters(BirGraphValue node, OptionalField<BirTypeParameter[]> value)
    {
        switch (node) {
            case BirClassDefinition target: target.TypeParams = value; break;
            case BirInterfaceDefinition target: target.TypeParams = value; break;
            case BirEnumDefinition target: target.TypeParams = value; break;
            case BirRefTypeDeclaration target: target.TypeParams = value; break;
            case BirFunctionDeclaration target: target.TypeParams = value; break;
            case BirInheritedImplementation target: target.TypeParams = value; break;
            case BirClosureDefinition target: target.TypeParams = value; break;
            case BirNewSuspendLambda target: target.TypeParams = value; break;
            default: throw Unexpected(node);
        }
    }
    internal static SourceTypeFacts TypeFacts(BirGraphValue node) => node switch {
        BirClassDefinition value => value.KotlinSupertypesPre.OrDefault(),
        BirInterfaceDefinition value => value.KotlinSupertypesPre.OrDefault(),
        BirEnumDefinition value => value.KotlinSupertypesPre.OrDefault(),
        BirRefTypeDeclaration value => value.KotlinSupertypesPre.OrDefault(),
        BirClosureDefinition value => value.KotlinSupertypesPre.OrDefault(), _ => null,
    };
    internal static void SetTypeFacts(BirGraphValue node, SourceTypeFacts value)
    {
        switch (node) {
            case BirClassDefinition target: target.KotlinSupertypesPre = value; break;
            case BirInterfaceDefinition target: target.KotlinSupertypesPre = value; break;
            case BirEnumDefinition target: target.KotlinSupertypesPre = value; break;
            case BirRefTypeDeclaration target: target.KotlinSupertypesPre = value; break;
            case BirClosureDefinition target: target.KotlinSupertypesPre = value; break;
            default: throw Unexpected(node);
        }
    }
    internal static SourceBoundsSnapshot BoundsFacts(BirGraphValue node) => node switch {
        BirFunctionDeclaration value => value.MethodTypeParameterBoundsPre.OrDefault(),
        BirInheritedImplementation value => value.MethodTypeParameterBoundsPre.OrDefault(), _ => null,
    };
    internal static void SetBoundsFacts(BirGraphValue node, SourceBoundsSnapshot value)
    {
        switch (node) {
            case BirFunctionDeclaration target: target.MethodTypeParameterBoundsPre = value; break;
            case BirInheritedImplementation target: target.MethodTypeParameterBoundsPre = value; break;
            default: throw Unexpected(node);
        }
    }
    static InvalidOperationException Unexpected(BirGraphValue node) => new("Unaccounted nullable declaration: " + node.GetType().Name);
}
