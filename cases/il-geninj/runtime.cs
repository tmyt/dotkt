using System.Collections.Generic;
namespace P {
    public class Item { public Item(string n) { Name = n; } public string Name { get; } }
    // (3) A property typed as a CONSTRUCTED GENERIC (`List<Item>`) — chained `.Add`/`.Count`/indexer must resolve
    // through the injected open `List<T>` applied to the injected `Item` (was `Any?` before P1-2).
    public class Bag { public List<Item> Items { get; } = new List<Item>(); }
}
