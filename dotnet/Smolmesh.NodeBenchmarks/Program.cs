using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Smolmesh.NodeBenchmarks;

// In process: BenchmarkDotNet 0.15 cannot launch a .NET 11 child process.
var config = DefaultConfig.Instance.AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
BenchmarkSwitcher.FromAssembly(typeof(ProcessPacketBenchmarks).Assembly).Run(args, config);
