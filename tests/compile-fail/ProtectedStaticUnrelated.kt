package ProtectedStaticNegative
fun unrelated() {
    Base.Field = 1
    Base.Method()
    Base.Property = 2
}
