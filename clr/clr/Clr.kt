// The @Clr binding annotation, available to the stdlib's CLR actuals so they can bind to BCL types/members.
// Mirrors the facadegen-generated `clr/_Clr.kt` used by user projects. kotc recognizes it by the FQN `clr.Clr`:
//  - on a CLASS  -> the declaration resolves to the named .NET type (e.g. @Clr("System.Text.StringBuilder")).
//  - on a MEMBER -> the member binds to the named .NET member; an unannotated member rolls up to its own name.
//  - on a TOP-LEVEL fun -> binds to a STATIC .NET method, splitting "Namespace.Type.Method" at the last '.'.
package clr

@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
public annotation class Clr(val name: String)
