using Smolmesh;
using Xunit;

public class ConfigurationTests
{
    [Fact]
    public void RejectsInvalidInputs()
    {
        Assert.Throws<InvalidDataException>(() => new Configuration().Validate());
        Assert.Throws<InvalidDataException>(() => new Configuration { NodeName = "test", IPv4Addresses = ["::1"] }.Validate());
        Assert.Throws<InvalidDataException>(() => new Configuration { NodeName = "test", Transports = [new() { Type = "other", Port = 1 }] }.Validate());
        new Configuration { NodeName = "test" }.Validate();
    }
}
