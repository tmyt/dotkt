using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void NativeTemplatesSourceSelfTest()
    {
        static void Need(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Native template binding: " + reason); }
        static TypeNode.Fqn Type(string name, params TypeNode[] arguments)
            => new(name, arguments.Length == 0 ? null : arguments);
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Native template binding accepted an incomplete selected declaration");
        }
        var previousTypes = _localTypes;
        try
        {
            var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            _localTypes = new HashSet<string>();
            void Bind(BirGraphValue node) => Apply(node, references, _localTypes);
            var integer = Type("System.Int32"); var text = Type("System.String");
            var local = new BirLocal { Name = "operand", Sty = integer };
            var elements = new IBirExpression[] { local };
            var literal = new BirNewList { Elem = integer, Elems = elements };
            Bind(literal);
            Need(literal.CtorRef.Value.Name.Value == ".ctor" && literal.AddRef.Value.Name.Value == "Add"
                && literal.AddRef.Value.ParameterTypes.Value.Single() is TypeNode.Tv { Scope: "type", I: 0 }
                && literal.CtorRef.Value.DeclaringType.Value is TypeNode.Fqn { Args: [var listElement] } && listElement == integer
                && ReferenceEquals(literal.Elems.Value, elements) && ReferenceEquals(elements[0], local),
                "collection literal declaration slots or actual elements changed");
            var listReference = literal.CtorRef.Value;
            Bind(literal);
            Need(ReferenceEquals(listReference, literal.CtorRef.Value), "literal rebinding is not idempotent");

            var part = new BirSpreadPart { Spread = false, E = OptionalField<IBirExpression>.Of(local) };
            var parts = new[] { part };
            var spread = new BirSpreadConcat { Elem = text, Parts = parts };
            Bind(spread);
            Need(spread.CtorRef.HasValue && spread.AddRef.Value.Name.Value == "Add"
                && spread.AddRangeRef.Value.Name.Value == "AddRange" && spread.ToArrayRef.Value.Name.Value == "ToArray"
                && spread.AddRangeRef.Value.ParameterTypes.Value.Single() is TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 0 }] }
                && ReferenceEquals(spread.Parts.Value, parts) && ReferenceEquals(part.E.Value, local),
                "spread role references or operand ownership changed");
            var body = new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(local) } };
            var loop = new BirForEach { Elem = integer, Src = OptionalField<IBirExpression>.Of(local), Body = body, Var = "element" };
            Bind(loop);
            Need(loop.EnumerableGetRef.HasValue && loop.CurrentRef.HasValue && loop.EnumerableGetErasedRef.HasValue
                && loop.CurrentErasedRef.HasValue && loop.MoveNextRef.Value.Name.Value == "MoveNext"
                && loop.CurrentRef.Value.DeclaringType.Value is TypeNode.Fqn { Args: [var currentElement] } && currentElement == integer
                && loop.CurrentErasedRef.Value.DeclaringType.Value is TypeNode.Fqn { Args: null }
                && ReferenceEquals(loop.Src.Value, local) && ReferenceEquals(loop.Body.Value, body),
                "generic/erased enumeration operands or current forest changed");

            var wrapped = new BirNullableWrap { Elem = integer, E = OptionalField<IBirExpression>.Of(local) };
            var empty = new BirNullableNull { Elem = integer };
            var has = new BirNullableHasValue { Elem = integer, E = OptionalField<IBirExpression>.Of(local) };
            var read = new BirNullableValue { Elem = integer, E = OptionalField<IBirExpression>.Of(local) };
            var safe = new BirSafeValueCast { Elem = integer, E = OptionalField<IBirExpression>.Of(local) };
            foreach (var node in new BirGraphValue[] { wrapped, empty, has, read, safe }) Bind(node);
            Need(wrapped.CtorRef.Value.Name.Value == ".ctor" && empty.CtorRef.HasValue && safe.CtorRef.HasValue
                && has.HasValueRef.Value.Name.Value == "get_HasValue" && read.ValueRef.Value.Name.Value == "get_Value"
                && wrapped.CtorRef.Value.ParameterTypes.Value.Single() is TypeNode.Tv { Scope: "type", I: 0 }
                && ReferenceEquals(wrapped.E.Value, local) && ReferenceEquals(safe.E.Value, local),
                "nullable declaration roles or actual operand changed");

            foreach (var kind in new[] { "add", "remove" })
            {
                var eventBody = new BirEventAccessorBody { Kind = kind, DelegateType = Type("System.EventHandler") };
                Bind(eventBody);
                Need(eventBody.CombineRef.Value.Name.Value == "Combine" && eventBody.RemoveRef.Value.Name.Value == "Remove"
                    && eventBody.CompareExchangeRef.Value.GenericArity.Value == 1
                    && eventBody.CompareExchangeRef.Value.ParameterTypes.Value[0] is TypeNode.ByRef { Of: TypeNode.Tv { Scope: "method", I: 0 } },
                    "event CAS must name both delegate operations and the open generic exchange");
            }
            var raise = new BirEventAccessorBody { Kind = "raise", DelegateType = Type("System.EventHandler") };
            Bind(raise);
            Need(raise.InvokeRef.Value.Name.Value == "Invoke" && !raise.CombineRef.IsPresent, "event raise acquired CAS roles");

            var function = new TypeNode.Fn(false, text, Array.Empty<TypeNode>(), Clr: "System.Func");
            var receiver = new BirLocal { Name = "function", Sty = function };
            var invoke = new BirDelegateInvoke { FuncType = function, Recv = OptionalField<IBirExpression>.Of(receiver), Args = Array.Empty<IBirExpression>() };
            var initializerFunction = function with { Params = new TypeNode[] { integer } };
            var initializerValue = new BirLocal { Name = "initializer", Sty = initializerFunction };
            var initializer = new BirNewArrayInit { Elem = text, Init = OptionalField<IBirExpression>.Of(initializerValue) };
            Bind(invoke); Bind(initializer);
            Need(invoke.InvokeRef.Value.Name.Value == "Invoke" && initializer.InvokeRef.Value.Name.Value == "Invoke"
                && ReferenceEquals(invoke.Recv.Value, receiver) && ReferenceEquals(initializer.Init.Value, initializerValue),
                "stored array initializer or delegate receiver was reconstructed");
            var natural = new BirNewDelegate { FuncType = function, Method = "target" };
            var closure = new BirNewClosure { FuncType = function, ClosureType = Type("Closure"), Captures = Array.Empty<IBirExpression>() };
            var bound = new BirNewBoundDelegate { FuncType = function, Recv = OptionalField<IBirExpression>.Of(local) };
            var nativeReceiver = new BirLocal { Name = "text", Sty = text };
            var nativeBound = new BirBoundClrDelegate { FuncType = function, ClrType = text, Method = "ToString",
                Recv = OptionalField<IBirExpression>.Of(nativeReceiver) };
            var nativeStatic = new BirClrStaticDelegate { FuncType = function with { Params = new TypeNode[] { text, text } },
                ClrType = text, Method = "Concat", ArgTypes = new TypeNode[] { text, text } };
            foreach (var node in new BirGraphValue[] { natural, closure, bound, nativeBound, nativeStatic }) Bind(node);
            Need(natural.DelegateCtorRef.Value.Name.Value == ".ctor" && closure.DelegateCtorRef.HasValue
                && bound.DelegateCtorRef.HasValue && bound.InvokeRef.HasValue && nativeBound.DelegateCtorRef.HasValue
                && nativeStatic.DelegateCtorRef.Value.DeclaringType.Value is TypeNode.Fqn { Args: [_, _, var staticResult] } && staticResult == text
                && ReferenceEquals(bound.Recv.Value, local) && ReferenceEquals(nativeBound.Recv.Value, nativeReceiver),
                "delegate construction roles, explicit function precedence or receiver identity changed");
            var ownFunction = new BirNewArrayInit { FuncType = function, Init = OptionalField<IBirExpression>.Of(
                new BirLocal { Name = "other", Sty = Type("System.Action") }) };
            Bind(ownFunction);
            Need(ownFunction.InvokeRef.Value.DeclaringType.Value is TypeNode.Fqn { Args: [var ownResult] } && ownResult == text,
                "initializer operand stamp outranked the operation's own selected function");

            // ContainsKey guards accept explicit null as already authored. Never
            // complete another role merely because its sibling ref is null.
            var presentList = new BirNewList { CtorRef = OptionalField<BirMemberReference>.Null };
            var presentLoop = new BirForEach { MoveNextRef = OptionalField<BirMemberReference>.Null };
            var presentWrap = new BirNullableWrap { CtorRef = OptionalField<BirMemberReference>.Null };
            var presentCall = new BirDelegateInvoke { InvokeRef = OptionalField<BirMemberReference>.Null };
            var presentConstruction = new BirNewClosure { DelegateCtorRef = OptionalField<BirMemberReference>.Null };
            var presentEvent = new BirEventAccessorBody { Kind = "add", CombineRef = OptionalField<BirMemberReference>.Null };
            foreach (var node in new BirGraphValue[] { presentList, presentLoop, presentWrap, presentCall, presentConstruction, presentEvent })
                Bind(node);
            Need(presentList.CtorRef.IsNull && !presentList.AddRef.IsPresent && presentLoop.MoveNextRef.IsNull
                && !presentLoop.CurrentRef.IsPresent && presentWrap.CtorRef.IsNull && presentCall.InvokeRef.IsNull
                && presentConstruction.DelegateCtorRef.IsNull && !presentEvent.RemoveRef.IsPresent,
                "explicit-null presence guard changed");
            var absentElement = new BirNullableValue(); Bind(absentElement);
            Need(!absentElement.ValueRef.IsPresent, "missing nullable element authored a member");
            Reject(() => Bind(new BirDelegateInvoke()));
            Reject(() => Bind(new BirSpreadConcat()));

            var array = ResolveOwnerType(Type("System.Array"), refs: references);
            var convert = array.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(method =>
                method.Name == "ConvertAll" && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 2);
            var callback = new BirNewDelegate { FuncType = new TypeNode.Fn(false, text, new TypeNode[] { integer }) };
            var originalArguments = new IBirExpression[] { local, callback };
            StampNativeDelegateArgumentTargets(references, originalArguments, new TypeNode[] { integer, text }, convert, Array.Empty<TypeNode>());
            Need(callback.DotktDelegateSlot.Value is TypeNode.Fqn { Name: "System.Converter`2", Args: [var input, var output] }
                && input == integer && output == text && ReferenceEquals(originalArguments[1], callback),
                "selected generic method's delegate slot lost exact method substitution");
            callback.DotktDelegateSlot = default;
            StampNativeDelegateArgumentTargets(references, originalArguments, new TypeNode[] { integer }, convert, Array.Empty<TypeNode>());
            StampNativeDelegateArgumentTargets(references, originalArguments, new TypeNode[] { integer, null }, convert, Array.Empty<TypeNode>());
            Need(!callback.DotktDelegateSlot.IsPresent, "incomplete callee method frame stamped a caller slot");
            var list = ResolveOwnerType(Type("System.Collections.Generic.List", integer), refs: references);
            var forEach = list.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(method => method.Name == "ForEach" && method.GetParameters().Length == 1);
            StampNativeDelegateArgumentTargets(references, new IBirExpression[] { callback }, Array.Empty<TypeNode>(), forEach, new TypeNode[] { integer });
            Need(callback.DotktDelegateSlot.Value is TypeNode.Fqn { Name: "System.Action`1", Args: [var actionInput] }
                && actionInput == integer, "owner frame did not replace the prior selected delegate slot");
            var thread = ResolveOwnerType(Type("System.Threading.Thread"), refs: references);
            var threadConstructor = thread.GetConstructors().Single(constructor => constructor.GetParameters() is [var parameter]
                && parameter.ParameterType.FullName == "System.Threading.ThreadStart");
            var start = new BirNewDelegate { FuncType = new TypeNode.Fn(false, Type("void"), Array.Empty<TypeNode>()) };
            StampNativeDelegateArgumentTargets(references, new IBirExpression[] { start }, Array.Empty<TypeNode>(), threadConstructor, Array.Empty<TypeNode>());
            Need(start.DotktDelegateSlot.Value is TypeNode.Fqn { Name: "System.Threading.ThreadStart" },
                "constructor MethodBase did not supply the exact native delegate slot");
            var readMethod = ResolveOwnerType(Type("System.Runtime.InteropServices.MemoryMarshal"), refs: references).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == "Read" && method.IsGenericMethodDefinition && method.GetParameters().Length == 1);
            var facts = NativeResolvedMethodTypeParameters(readMethod, 1);
            Need(facts.Value.Single().SpecialConstraints.Value.Contains("new")
                && facts.Value[0].SpecialConstraints.Value.Contains("struct")
                && facts.Value[0].Constraints.Value.Contains(Type("System.ValueType"))
                && !NativeResolvedMethodTypeParameters(readMethod, 0).IsPresent,
                "selected native generic flags or absent type arguments changed");
            Reject(() => NativeResolvedMethodTypeParameters(readMethod, 2));
            facts.Value[0].Name = "mutated";
            Need(NativeResolvedMethodTypeParameters(readMethod, 1).Value[0].Name.Value != "mutated", "selected method facts were shared mutable output");
        }
        finally { _localTypes = previousTypes; }
        Console.WriteLine("[typed native templates] self-test OK (roles, open frames, exact presence and operand identity)");
    }
}
