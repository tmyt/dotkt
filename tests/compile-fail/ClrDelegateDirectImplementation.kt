import ClrDelegateDirectImplementation.Callback

class InvalidDelegateImplementation : Callback {
    override fun invoke(value: Any?) = Unit
}
