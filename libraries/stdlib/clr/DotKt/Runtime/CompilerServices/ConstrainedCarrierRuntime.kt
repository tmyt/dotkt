package DotKt.Runtime.CompilerServices

// The compiler allocates one unique private thunk name on the exact constructed owner. Selection uses that
// declaration identity only; neither overload resolution nor type inference depends on argument values.
// MakeGenericMethod validates the original owner-dependent constraints using the actual CLR method arguments.
// Calling the resulting typed delegate preserves ref aliasing and ordinary exception propagation.
@PublishedApi
internal fun constrainedCarrierDelegate(
    receiver: Any,
    owner: StarProjectionType,
    thunkName: String,
    methodTypes: Array<StarProjectionType>,
    delegateType: StarProjectionType,
): StarProjectionDelegate {
    var selected: StarProjectionMethod? = null
    for (method in owner.getMethods(StarProjectionBindingFlags.DECLARED_INSTANCE_PUBLIC_NON_PUBLIC)) {
        if (method.name != thunkName) continue
        check(selected == null) { "Duplicate constrained carrier thunk " + thunkName }
        selected = method
    }
    val target = selected ?: error("Missing constrained carrier thunk " + thunkName)
    return target.makeGenericMethod(methodTypes).createDelegate(delegateType, receiver)
}
