using BenchmarkDotNet.Running;

namespace LiteDocumentStore.Benchmarks;

internal sealed class Program
{
    static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
