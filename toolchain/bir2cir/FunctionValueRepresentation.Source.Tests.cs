using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FunctionValueRepresentation
{
    static void SelfTestSourceRepresentation()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var refLike = new TypeNode.Fqn("RefLike");
        var selected = new TypeNode.Fn(false, integer, new TypeNode[] { integer }, Clr: "System.Func");
        var ordinary = new TypeNode.Fn(false, new TypeNode.Nullable(integer),
            new TypeNode[] { integer, new TypeNode.Oblivious(integer), refLike }, integer,
            Ctx: new TypeNode[] { integer });
        var native = new TypeNode.Fqn(FunctionPointerVocabulary.Pointer, new TypeNode[] { ordinary });
        var suspended = new TypeNode.Fn(true, ordinary, new TypeNode[] { ordinary });
        ValueTypeOracle isByRefLike = type => type.Name == refLike.Name;
        var physical = (TypeNode.Fn)RepresentType(ordinary, isByRefLike);
        if (physical.Ret != new TypeNode.Nullable(Object) || physical.Params[0] != Object
            || physical.Params[1] != new TypeNode.Oblivious(Object) || physical.Params[2] != refLike
            || physical.Recv != Object || physical.Ctx != null
            || !ReferenceEquals(RepresentType(native, isByRefLike), native)
            || !ReferenceEquals(RepresentType(suspended, isByRefLike), suspended)
            || !ReferenceEquals(RepresentType(selected, isByRefLike), selected))
            throw new InvalidOperationException("Function carriers changed nullability, unboxable slots or native/suspend boundaries");

        var parameter = new BirParameter { Name = "callback", Type = ordinary };
        var call = new BirCallInstance { Method = "accept", OwnerType = new TypeNode.Fqn("Owner"),
            Sig = new TypeNode[] { ordinary, native }, MemberSignature = new TypeNode[] { ordinary },
            MemberReturnType = ordinary, Ret = ordinary,
            Recv = OptionalField<IBirExpression>.Of(new BirLocal { Name = "owner" }),
            Args = Array.Empty<IBirExpression>(),
        };
        var invocation = new BirDelegateInvoke { FuncType = new TypeNode.Fn(false, ordinary, new TypeNode[] { integer }),
            Recv = OptionalField<IBirExpression>.Of(new BirLocal { Name = "callback" }),
            Args = new IBirExpression[] { new BirCallStatic { Owner = new TypeNode.Fqn("Owner"), Method = "once", Ret = integer,
                Args = Array.Empty<IBirExpression>() } },
        };
        var construction = new BirNewClosure { ClosureType = new TypeNode.Fqn("Closure"), Method = "invoke",
            FuncType = ordinary, DotktDelegateSlot = new TypeNode.Fqn("Callback"),
            Captures = new IBirExpression[] { new BirLocal { Name = "capture", Sty = ordinary } },
        };
        var method = new BirFunctionDeclaration { Name = "method", Params = new[] { parameter }, Ret = ordinary,
            Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = call },
                new BirExpressionStatement { Expr = invocation },
                new BirExpressionStatement { Expr = construction },
            },
            Attrs = new[] { DefaultBirCarrier.Create(0, new BirLocal { Name = "default", Sty = ordinary }) },
        };
        var constructor = new BirConstructor { Params = new[] { new BirParameter { Name = "callback", Type = ordinary } },
            DelegationSig = new TypeNode[] { ordinary }, Body = Array.Empty<IBirStatement>(),
        };
        var inherited = new BirInheritedClassMethod { Member = "inherited", Ret = ordinary,
            Params = new[] { new BirParameter { Name = "callback", Type = ordinary } } };
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", Methods = new[] { method },
            Ctors = new[] { constructor }, InheritedClassMethods = new[] { inherited },
            Fields = new[] { new BirFieldDeclaration { Name = "callback", Type = new TypeNode.Array(ordinary), KotlinType = ordinary } },
        };
        var root = new BirFileDeclaration { FileClass = "Functions", Types = new IBirTypeDeclaration[] { owner } };
        var expected = root.ToDom();
        Apply(expected, isByRefLike);
        var methods = owner.Methods.Value;
        var parameters = method.Params.Value;
        var inheritedBefore = inherited.DeepClone();
        var attributes = method.Attrs.Value;
        var attributesBefore = attributes[0].DeepClone();
        var result = Apply(root, isByRefLike);
        var restoration = (BirCast)((BirExpressionStatement)method.Body.Value[1]).Expr.Value;
        if (!ReferenceEquals(result, root) || !ReferenceEquals(root.Types.Value[0], owner)
            || !ReferenceEquals(owner.Methods.Value, methods) || !ReferenceEquals(methods[0], method)
            || !ReferenceEquals(method.Params.Value, parameters) || !ReferenceEquals(parameters[0], parameter)
            || !ReferenceEquals(restoration.E.Value, invocation)
            || !restoration._exactBridgeCast.OrDefault() || !restoration.FunctionValueRestoration.OrDefault()
            || !TypeNodeDataEquality.Equal(restoration.Type.Value, physical)
            || !TypeNodeDataEquality.Equal(parameter.FunctionSignatureType.Value, ordinary)
            || !TypeNodeDataEquality.Equal(parameter.Type.Value, physical)
            || !TypeNodeDataEquality.Equal(call.FunctionCallSignature.Value[0], ordinary)
            || !TypeNodeDataEquality.Equal(call.MemberSignature.Value[0], ordinary)
            || !TypeNodeDataEquality.Equal(call.MemberReturnType.Value, physical)
            || !TypeNodeDataEquality.Equal(constructor.DelegationSig.Value[0], ordinary)
            || !TypeNodeDataEquality.Equal(construction.FuncType.Value, ordinary)
            || !TypeNodeDataEquality.Equal(((BirLocal)construction.Captures.Value[0]).Sty.Value, physical)
            || !inherited.StructurallyEquals(inheritedBefore)
            || !ReferenceEquals(method.Attrs.Value, attributes) || !attributes[0].StructurallyEquals(attributesBefore)
            || !JsonNode.DeepEquals(expected, root.ToDom()))
            throw new InvalidOperationException("Owning function carrier mutation lost identity, facts, boundaries or late-consumer parity");
        var once = 0;
        root.Visit(node => { if (node is BirCallStatic { Method.HasValue: true } value && value.Method.Value == "once") once++; });
        if (once != 1) throw new InvalidOperationException("Function result restoration duplicated invocation operands");
        var absent = new BirCallStatic { Method = "absent", Sig = OptionalField<TypeNode[]>.Null };
        Apply(absent, isByRefLike);
        if (!absent.Sig.IsNull || absent.FunctionCallSignature.IsPresent)
            throw new InvalidOperationException("Function signature capture changed absent/null vector facts");
        foreach (var declared in new BirGraphValue[] {
            new BirNewClosure { FuncType = ordinary, DotktDelegateSlot = new TypeNode.Fqn("Callback") },
            new BirNewDelegate { FuncType = ordinary, DotktDelegateSlot = new TypeNode.Fqn("Callback") },
            new BirNewBoundDelegate { FuncType = ordinary, DotktDelegateSlot = new TypeNode.Fqn("Callback") },
            new BirBoundClrDelegate { FuncType = ordinary, DotktDelegateSlot = new TypeNode.Fqn("Callback") },
            new BirClrStaticDelegate { FuncType = ordinary, DotktDelegateSlot = new TypeNode.Fqn("Callback") },
        })
        {
            var declaredExpected = declared.ToDom();
            Apply(declaredExpected, isByRefLike);
            var represented = Apply(declared, isByRefLike);
            if (!represented.HasDeclaredDelegateSlot || !JsonNode.DeepEquals(represented.ToDom(), declaredExpected))
                throw new InvalidOperationException("Declared delegate signatures diverged from schema-owned slot presence");
        }
        Console.WriteLine("[owning function values] self-test OK (carrier mutation, declaration/call facts, exact restoration, once evaluation, boundaries, identity and presence)");
    }
}
