using Serilog;
using Serilog.Events;

namespace Smolmesh;

internal static class Logging
{
    internal static void Configure() => Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console(
            restrictedToMinimumLevel: LogEventLevel.Information,
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}",
            standardErrorFromLevel: LogEventLevel.Verbose)
        .CreateLogger();
}
