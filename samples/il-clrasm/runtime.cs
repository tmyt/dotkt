using System.Collections.Generic;
namespace P {
    public class Item { public Item(string n) { Name = n; } public string Name { get; } }
    public class Bag { public List<Item> Items { get; } = new List<Item>(); }
    // The everyday .NET case: a method taking IEnumerable<T>, called with a List<T>.
    public class Sink { public int Count(IEnumerable<Item> xs) { int n = 0; foreach (var x in xs) n++; return n; } }
}
