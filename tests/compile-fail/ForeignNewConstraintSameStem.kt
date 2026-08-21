import DeclarationIdentityInterop.Constraints
import DeclarationIdentityInterop.ConstructorCollision

fun main() {
    // ConstructorCollision<T> is a struct and therefore satisfies CLR new(). Its non-generic same-stem sibling has
    // only a private constructor and must not borrow that declaration fact.
    Constraints.NeedsNew<ConstructorCollision>()
}
