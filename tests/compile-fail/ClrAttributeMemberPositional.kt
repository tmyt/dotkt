import AttributeArguments.ChoiceAttribute
import AttributeArguments.FlagAttribute

@ChoiceAttribute("x") class PositionalProperty
@FlagAttribute(7, true) class PositionalField
@AttributeArguments.IntsAttribute(intArrayOf(1, 2)) class PositionalArray
