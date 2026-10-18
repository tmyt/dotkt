namespace NativeChar

type Code =
    | First = 'z'
    | Second = 'a'
    | Alias = 'z'

type Api =
    static member First() = Code.First
    static member Bits(value: Code) = System.Convert.ToInt32(value)
