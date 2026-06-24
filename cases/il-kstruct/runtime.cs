using System; using System.Threading.Tasks;
namespace Kfc { public static class Api {
    public static Task<int> Fetch(int ms, int v) => Task.Run(async () => { await Task.Delay(ms); return v; });
} }
