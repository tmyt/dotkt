import NUnit.Framework.TestAttribute
import ConstructorSelectionInterop.SelectedConstructor

class ClrSelectedConstructorTests {
    @TestAttribute
    fun selectedDeclarationSurvivesClrConstructionLowering() {
        check(SelectedConstructor<Int>(value = 3).Chosen == 1)
        check(SelectedConstructor<Int>(marker = 5).Chosen == 2)
        check(SelectedConstructor<String>(value = "value").Chosen == 1)
        check(SelectedConstructor<String>(marker = 7).Chosen == 2)
    }
}
