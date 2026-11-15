using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void NativeStorageSourceSelfTest()
    {
        static void Need(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Native storage binding: " + reason); }
        static TypeNode.Fqn Type(string name, params TypeNode[] arguments)
            => new(name, arguments.Length == 0 ? null : arguments);
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Native storage binding accepted a missing or ambiguous declaration");
        }
        var priorReferences = _refs; var priorLocals = _localTypes;
        void Resolve(BirGraphValue node) => Apply(node, _refs, _localTypes);
        try
        {
            _refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            var localTypes = new HashSet<string>(StringComparer.Ordinal);
            _localTypes = localTypes;
            var text = Type("System.String"); var integer = Type("System.Int32"); var foreignVoid = Type("System.Void");
            var list = Type("System.Collections.Generic.IReadOnlyList", text);
            var receiver = new BirLocal { Name = "actual", Sty = list };
            var read = new BirPropertyGet { Type = list, Name = "Count", Recv = receiver, Ret = integer,
                DeclarationId = "selected-before-physical-storage" };
            Resolve(read);
            Need(read.Type.Value is TypeNode.Fqn { Name: "System.Collections.Generic.IReadOnlyCollection", Args: [var argument] }
                && argument == text && read.Member.Value == "accessor" && read.Accessor.Value == "get_Count"
                && read.MemberRef.Value.Kind.Value == MemberRefNode.Kinds.PropertyAccessor
                && read.MemberRef.Value.DeclaringType.Value is TypeNode.Fqn { Args: [var referenceArgument] } && referenceArgument == text
                && read.ResolvedMemberReturn.Value == integer && !read.DeclarationId.IsPresent
                && ReferenceEquals(read.Recv.Value, receiver) && read.Ret.Value == integer,
                "base-interface owner projection, selected declaration, return stamp or actual receiver changed");

            var listClass = Type("System.Collections.Generic.List", text);
            var capacity = new BirLocal { Name = "capacity", Sty = integer };
            var setter = new BirPropertySet { Type = listClass, Name = "Capacity", Value = capacity,
                Recv = new BirLocal { Name = "list", Sty = listClass }, DeclarationId = OptionalField<string>.Null };
            Resolve(setter);
            Need(setter.Accessor.Value == "set_Capacity" && setter.Member.Value == "accessor"
                && setter.MemberRef.Value.ParameterTypes.Value.Single() == integer
                && setter.ResolvedMemberReturn.Value == foreignVoid && !setter.DeclarationId.IsPresent
                && ReferenceEquals(setter.Value.Value, capacity), "property setter physical slot or value ownership changed");

            var now = new BirPropertyGet { Type = Type("System.DateTime"), Name = "Now", Static = true, Dispatch = "authored" };
            Resolve(now);
            Need(now.MemberRef.Value.Name.Value == "get_Now" && now.Dispatch.Value == "authored",
                "static property binding overwrote a dispatch fact the old owner did not write");
            var superMessage = new BirPropertyGet { Type = Type("System.Exception"), Name = "Message", Super = true, Recv = new BirThis() };
            Resolve(superMessage);
            Need(superMessage.Dispatch.Value == "call" && superMessage.MemberRef.Value.Name.Value == "get_Message",
                "lexical super property call lost direct base dispatch");
            var tuple = Type("System.ValueTuple", integer, text);
            var tupleReceiver = new BirLocal { Name = "tuple", Sty = tuple };
            var directProperty = new BirPropertyGet { Type = tuple, Name = "Item1", Recv = tupleReceiver };
            var field = new BirFieldGet { OwnerType = tuple, Name = "Item1", Recv = tupleReceiver };
            var fieldValue = new BirLocal { Name = "value", Sty = integer };
            var fieldWrite = new BirFieldSetExpression { OwnerType = tuple, Name = "Item1", Recv = tupleReceiver, Value = fieldValue };
            var statementWrite = new BirSetField { OwnerType = tuple, Name = "Item1", Recv = tupleReceiver, Value = fieldValue };
            foreach (var node in new BirGraphValue[] { directProperty, field, fieldWrite, statementWrite }) Resolve(node);
            Need(directProperty.Member.Value == "field" && directProperty.MemberRef.Value.ReturnType.Value is TypeNode.Tv { Scope: "type", I: 0 }
                && field.MemberRef.Value.Kind.Value == MemberRefNode.Kinds.Field && !field.Member.IsPresent
                && field.ResolvedMemberReturn.Value is TypeNode.Tv { Scope: "type", I: 0 }
                && fieldWrite.MemberRef.Value.Name.Value == "Item1" && statementWrite.MemberRef.Value.Name.Value == "Item1"
                && ReferenceEquals(fieldWrite.Value.Value, fieldValue) && ReferenceEquals(statementWrite.Value.Value, fieldValue)
                && ReferenceEquals(field.Recv.Value, tupleReceiver), "field spelling, open declaration slots or actual operands changed");

            var accessorField = new BirFieldGet { OwnerType = listClass, Name = "Count", Recv = receiver };
            Resolve(accessorField);
            Need(accessorField.Member.Value == "accessor" && accessorField.Accessor.Value == "get_Count"
                && accessorField.MemberRef.Value.Kind.Value == MemberRefNode.Kinds.PropertyAccessor,
                "external backing storage bypassed its actual accessor");
            var exactField = field.MemberRef.Value;
            var exact = new BirFieldSetExpression { OwnerType = Type("NotAReferenceType"), Name = "not-looked-up",
                MemberRef = exactField, Value = fieldValue, ResolvedMemberReturn = OptionalField<TypeNode>.Null };
            Resolve(exact);
            Need(ReferenceEquals(exact.MemberRef.Value, exactField) && exact.ResolvedMemberReturn.IsNull
                && ReferenceEquals(exact.Value.Value, fieldValue), "exact selected family field was rebound or restamped");
            var delegateValue = new BirNewDelegate { Method = "target",
                FuncType = new TypeNode.Fn(false, Type("void"), Array.Empty<TypeNode>(), Clr: "System.Action") };
            var delegateWrite = new BirFieldSetExpression {
                OwnerType = Type("System.ValueTuple", Type("System.Threading.ThreadStart"), integer),
                Name = "Item1", Value = delegateValue };
            Resolve(delegateWrite);
            Need(ReferenceEquals(delegateWrite.Value.Value, delegateValue)
                && delegateValue.DotktDelegateSlot.Value is TypeNode.Fqn { Name: "System.Threading.ThreadStart" },
                "generic written delegate slot did not use the actual closed owner frame and actual construction");
            localTypes.Add("System.String");
            var stale = new BirFieldGet { OwnerType = text, Name = "private-local-storage" };
            var localStatic = new BirStaticFieldGet { OwnerType = text, Name = "Empty" };
            Resolve(stale); Resolve(localStatic);
            Need(!stale.MemberRef.IsPresent && !localStatic.FieldRef.IsPresent, "same-emission storage bound a stale reference twin");
            localTypes.Clear();
            var staticRead = new BirStaticFieldGet { OwnerType = text, Name = "Empty" };
            var staticWrite = new BirStaticFieldSet { OwnerType = text, Name = "Empty", Value = fieldValue };
            var nullReference = new BirStaticFieldGet { OwnerType = text, Name = "Empty", FieldRef = OptionalField<BirMemberReference>.Null };
            foreach (var node in new BirGraphValue[] { staticRead, staticWrite, nullReference }) Resolve(node);
            Need(staticRead.FieldRef.Value.Name.Value == "Empty" && staticRead.FieldRef.Value.DeclaringType.Value == text
                && staticWrite.FieldRef.Value.Name.Value == "Empty" && ReferenceEquals(staticWrite.Value.Value, fieldValue)
                && nullReference.FieldRef.IsNull, "static storage or explicit-null field identity guard changed");

            var appDomain = Type("System.AppDomain");
            var handler = new BirLocal { Name = "storedHandler", Sty = Type("System.AssemblyLoadEventHandler") };
            var add = new BirEventAdd { Type = appDomain, Event = "AssemblyLoad", Handler = handler, Recv = receiver };
            var remove = new BirEventRemove { Type = appDomain, Event = "AssemblyLoad", Handler = handler, Recv = receiver };
            Resolve(add); Resolve(remove);
            Need(add.Accessor.Value == "add_AssemblyLoad" && remove.Accessor.Value == "remove_AssemblyLoad"
                && add.MemberRef.Value.Kind.Value == MemberRefNode.Kinds.EventAccessor
                && add.ResolvedMemberReturn.Value == foreignVoid && remove.ResolvedMemberReturn.Value == foreignVoid
                && add.Handler.Value is BirCast addCast && ReferenceEquals(addCast.E.Value, handler)
                && remove.Handler.Value is BirCast removeCast && ReferenceEquals(removeCast.E.Value, handler)
                && ReferenceEquals(add.Recv.Value, receiver), "event accessor, foreign void or actual handler MOVE changed");
            var localEvent = new BirEventAdd { LocalAccessor = true, Type = new TypeNode.Tv("method", 0),
                DelegateType = handler.Sty, Accessor = "localAdd", AccessorOwner = Type("LocalOwner"), Dispatch = "constrained", Handler = handler };
            Resolve(localEvent);
            Need(ReferenceEquals(localEvent.Handler.Value, handler) && !localEvent.MemberRef.IsPresent,
                "already-bound local constrained event was re-resolved");
            Reject(() => Resolve(new BirEventAdd { LocalAccessor = true }));
            localTypes.Add("System.AppDomain");
            Reject(() => Resolve(new BirEventAdd { Type = appDomain, Event = "AssemblyLoad" }));
            localTypes.Clear();

            var implementation = new BirFunctionDeclaration { Name = "implementationName", PendingOverrideOwner = Type("System.Exception"),
                PendingOverrideMember = "get_Message", PendingOverrideReturn = text, Params = Array.Empty<BirParameter>() };
            Resolve(implementation);
            Need(implementation.RequiresClrOverride.Value && implementation.ClrOverrideRef.Value.Name.Value == "get_Message"
                && implementation.ClrOverrideRef.Value.ReturnType.Value == text && !implementation.PendingOverrideOwner.IsPresent
                && !implementation.PendingOverrideMember.IsPresent && !implementation.PendingOverrideReturn.IsPresent
                && implementation.Name.Value == "implementationName", "override selected base slot or descriptor consumption changed");
            var untouched = new BirFunctionDeclaration { PendingOverrideOwner = OptionalField<TypeNode>.Null };
            Resolve(untouched);
            Need(untouched.PendingOverrideOwner.IsNull && !untouched.RequiresClrOverride.IsPresent, "noncandidate override presence changed");
            Reject(() => Resolve(new BirFunctionDeclaration { Name = "get_Message",
                PendingOverrideOwner = Type("System.Exception"), Params = Array.Empty<BirParameter>() }));

            // Real CLR declarations pin the nearest-member/protected/interface
            // policies without a second DOM resolver or fabricated metadata.
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            Need(FindPropAccessor(typeof(NativeStorageNearestField), "Slot", false, flags) == null
                && FindFieldMember(typeof(NativeStorageNearestField), "Slot", flags)?.DeclaringType == typeof(NativeStorageNearestField)
                && FindPropAccessor(typeof(NativeStorageNearestProperty), "Slot", false, flags)?.DeclaringType == typeof(NativeStorageNearestProperty)
                && FindPropAccessor(typeof(NativeStorageProtected), "Slot", true, flags)?.IsFamily == true,
                "nearest field/property precedence or protected accessor visibility changed");
            Reject(() => FindEventAccessor(typeof(INativeStorageAmbiguousEvents), "Changed", true,
                BindingFlags.Public | BindingFlags.Instance));
            Console.WriteLine("[native storage binding] self-test OK (properties, fields, events, exact overrides, presence and owning operands)");
        }
        finally { _refs = priorReferences; _localTypes = priorLocals; }
    }
}

class NativeStoragePropertyBase { public virtual object Slot { get; set; } }
class NativeStorageNearestField : NativeStoragePropertyBase { public new object Slot = null; }
class NativeStorageFieldBase { public object Slot = null; }
class NativeStorageNearestProperty : NativeStorageFieldBase { public new object Slot { get; set; } }
class NativeStorageProtected { public object Slot { get; protected set; } }
interface INativeStorageEventLeft { event Action Changed; }
interface INativeStorageEventRight { event Action Changed; }
interface INativeStorageAmbiguousEvents : INativeStorageEventLeft, INativeStorageEventRight { }
