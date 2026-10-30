using Smolmesh;
using Xunit;

public class ProgramTests
{
    [Theory]
    [InlineData("bogus")]
    [InlineData("name-to-ipv6")]
    [InlineData("meshnode", "/nonexistent/smolmesh-config.json")]
    public async Task ErrorsArePrintedToStderr(params string[] args)
    {
        using var output = new StringWriter();
        Logging.Configure();
        TextWriter previous = Console.Error;
        Console.SetError(output);
        int exitCode;
        try { exitCode = await Program.RunAsync(args, default); }
        finally { Console.SetError(previous); }
        Assert.Equal(1, exitCode);
        Assert.Contains("ERR", output.ToString());
        Assert.Contains(args[^1], output.ToString());
    }
}
