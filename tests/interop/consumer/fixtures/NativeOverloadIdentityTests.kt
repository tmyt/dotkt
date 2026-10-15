import NUnit.Framework.TestAttribute
import NativeOverloadIdentity.ISlot
import NativeOverloadIdentity.IReverseSlot
import NativeOverloadIdentity.Selection
import NativeOverloadIdentity.CompletedSlot
import NativeOverloadIdentity.ClassSlot
import NativeOverloadIdentity.IIndexedSlot
import NativeOverloadIdentity.IReverseIndexedSlot
import System.Threading.Tasks.Task
import System.Threading.Tasks.Task1
import System.Runtime.CompilerServices.TaskAwaiter1

private interface NativeReordered<A, B> : ISlot<B>
private interface NativeTwiceReordered<A, B> : NativeReordered<B, A>
private class NativeImplementation : NativeTwiceReordered<Int, String>
private class NativeDirectImplementation : ISlot<Int>
private class NativeStringImplementation : ISlot<String>
private interface NativeReverseReordered<A, B> : IReverseSlot<B>
private interface NativeReverseTwice<A, B> : NativeReverseReordered<B, A>
private class NativeReverseImplementation : NativeReverseTwice<Int, String>
private class NativeClassImplementation : ClassSlot<Int>()
private interface NativeIndexedReordered<A, B> : IIndexedSlot<B>
private class NativeIndexedImplementation : NativeIndexedReordered<String, Int>
private class NativeReverseIndexedImplementation : IReverseIndexedSlot<Int>

private fun <T> nativeOwnerEcho(slot: ISlot<T>, value: T): T = slot.Echo(value, "marker")
private fun <T> nativeIntEcho(slot: ISlot<T>): String = slot.Echo(41, "marker")
private fun <S : ISlot<Int>> nativeConstrainedPlain(slot: S): String = slot.Plain(211)
private fun <S : ISlot<Int>> nativeConstrainedEcho(slot: S): String = slot.Echo(223, "marker")
private fun <T> nativeOwnerReference(slot: ISlot<T>): (T, String) -> T = slot::Echo
private fun <T> nativeIntReference(slot: ISlot<T>): (Int, String) -> String = slot::Echo

class NativeOverloadIdentityTests {
    @TestAttribute fun originalTwoHopCollisionKeepsTheSelectedDeclaration() {
        check(NativeImplementation().Echo(38, "marker") == "int-overload")
    }

    @TestAttribute fun directAndInheritedCallsKeepReturnAgreement() {
        val direct: ISlot<Int> = NativeDirectImplementation()
        val inherited = NativeDirectImplementation()
        val first: String = direct.Echo(42, "marker")
        val second: String = inherited.Echo(43, "marker")
        check(first == "int-overload" && second == "int-overload")
        check(nativeOwnerEcho(direct, 47) == 47)
        check(nativeIntEcho(direct) == "int-overload")
        check(nativeOwnerEcho(NativeStringImplementation(), "owner") == "owner")
        val nativeClass = NativeClassImplementation()
        val boundClass: (Int, String) -> String = nativeClass::Echo
        val unboundClass: (NativeClassImplementation, Int, String) -> String = NativeClassImplementation::Echo
        check(nativeClass.Echo(151, "marker") == "class-int-overload")
        check(boundClass(157, "marker") == "class-int-overload")
        check(unboundClass(nativeClass, 163, "marker") == "class-int-overload")
    }

    @TestAttribute fun reverseMethodOrderKeepsTheSameSelectionRule() {
        val slot: IReverseSlot<Int> = NativeReverseImplementation()
        val direct: String = slot.Echo(53, "marker")
        val inherited: String = NativeReverseImplementation().Echo(59, "marker")
        check(direct == "reverse-int-overload" && inherited == "reverse-int-overload")
    }

    @TestAttribute fun boundReferencesKeepOpenDeclarationsBeforeOwnerSubstitution() {
        val direct: ISlot<Int> = NativeDirectImplementation()
        val first: (Int, String) -> String = direct::Echo
        val inherited: (Int, String) -> String = NativeImplementation()::Echo
        val reverse: (Int, String) -> String = NativeReverseImplementation()::Echo
        check(first(61, "marker") == "int-overload")
        check(inherited(67, "marker") == "int-overload")
        check(reverse(71, "marker") == "reverse-int-overload")
        check(nativeOwnerReference(direct)(73, "marker") == 73)
        check(nativeIntReference(direct)(79, "marker") == "int-overload")
    }

    @TestAttribute fun unboundReferencesKeepTheSelectedDeclarationAndOwnerFrame() {
        val direct: (ISlot<Int>, Int, String) -> String = ISlot<Int>::Echo
        val inherited: (NativeImplementation, Int, String) -> String = NativeImplementation::Echo
        val reverse: (NativeReverseImplementation, Int, String) -> String = NativeReverseImplementation::Echo
        check(direct(NativeDirectImplementation(), 83, "marker") == "int-overload")
        check(inherited(NativeImplementation(), 89, "marker") == "int-overload")
        check(reverse(NativeReverseImplementation(), 97, "marker") == "reverse-int-overload")
    }

    @TestAttribute fun nongenericMethodsAlsoKeepTheirOpenDeclarationIdentity() {
        val slot: ISlot<Int> = NativeDirectImplementation()
        val direct: String = slot.Plain(101)
        val inherited: String = NativeImplementation().Plain(103)
        val reverse: String = NativeReverseImplementation().Plain(107)
        val bound: (Int) -> String = NativeImplementation()::Plain
        val unbound: (ISlot<Int>, Int) -> String = ISlot<Int>::Plain
        check(direct == "plain-int-overload" && inherited == "plain-int-overload")
        check(reverse == "reverse-plain-int-overload")
        check(bound(109) == "plain-int-overload")
        check(unbound(slot, 113) == "plain-int-overload")
        check(nativeConstrainedPlain(NativeImplementation()) == "plain-int-overload")
        check(nativeConstrainedEcho(NativeImplementation()) == "int-overload")
    }

    @TestAttribute fun omittedDefaultsBelongToTheSelectedOverload() {
        val direct: ISlot<Int> = NativeDirectImplementation()
        check(direct.Optional<String>(127) == "int-default")
        check(NativeImplementation().Optional<String>(131) == "int-default")
        check(NativeReverseImplementation().Optional<String>(167) == "reverse-int-default")
    }

    @TestAttribute fun unitCoercionAdaptersKeepTheSelectedNativeTarget() {
        val bound: (Int, String) -> Unit = NativeImplementation()::Echo
        Selection.Last = "before-bound"
        bound(137, "marker")
        check(Selection.Last == "int")
        val reverse: (Int, String) -> Unit = NativeReverseImplementation()::Echo
        Selection.Last = "before-reverse"
        reverse(173, "marker")
        check(Selection.Last == "reverse-int")
        val reverseUnbound: (NativeReverseImplementation, Int, String) -> Unit = NativeReverseImplementation::Echo
        Selection.Last = "before-reverse-unbound"
        reverseUnbound(NativeReverseImplementation(), 179, "marker")
        check(Selection.Last == "reverse-int")
        val unbound: (NativeImplementation, Int, String) -> Unit = NativeImplementation::Echo
        Selection.Last = "before-unbound"
        unbound(NativeImplementation(), 139, "marker")
        check(Selection.Last == "int")
    }

    @TestAttribute fun inheritedReturnOnlyDifferencesKeepTheSelectedOwner() {
        val task = Task.FromResult(149)
        check(task.GetAwaiter().GetResult() == 149)
        val bound: () -> TaskAwaiter1<Int> = task::GetAwaiter
        val unbound: (Task1<Int>) -> TaskAwaiter1<Int> = Task1<Int>::GetAwaiter
        check(bound().GetResult() == 149)
        check(unbound(task).GetResult() == 149)
    }

    @TestAttribute fun hiddenInterfaceCompletionsKeepTheirProjectedFrame() {
        val value = CompletedSlot()
        check(value.Mirror("direct") == "direct")
        check(value.GenericMirror("generic", "marker") == "generic")
        val bound: (String) -> String = value::Mirror
        val unbound: (CompletedSlot, String) -> String = CompletedSlot::Mirror
        val generic: (String, String) -> String = value::GenericMirror
        check(bound("bound") == "bound")
        check(unbound(value, "unbound") == "unbound")
        check(generic("generic-ref", "marker") == "generic-ref")
    }

    @TestAttribute fun indexerOperatorsKeepTheSelectedDeclaration() {
        val direct: IIndexedSlot<Int> = NativeIndexedImplementation()
        check(direct[181] == "int-indexer")
        check(NativeIndexedImplementation()[191] == "int-indexer")
        check(NativeReverseIndexedImplementation()[193] == "reverse-int-indexer")
    }
}
