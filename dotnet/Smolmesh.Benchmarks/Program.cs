using BenchmarkDotNet.Running;
using Smolmesh.Benchmarks;

Verification.Run();
if (!args.Contains("--verify"))
    BenchmarkSwitcher.FromAssembly(typeof(PacketBenchmarks).Assembly).Run(args);
