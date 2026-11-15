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
        Assert.Throws<InvalidDataException>(() => new Configuration { NodeName = "test", Transports = [new() { Type = "udp1", Port = 1 }] }.Validate());
        Assert.Throws<InvalidDataException>(() => new Configuration { NodeName = "test", Transports = [new() { Type = "udp1", Port = 1, Peers = [new() { Host = "host", Port = 1 }] }] }.Validate());
        Assert.Throws<InvalidDataException>(() => new Configuration { NodeName = "test", Transports = [new() { Type = "udp1", Port = 1, Peers = [new() { Key = "key", Host = "host" }] }] }.Validate());
        Assert.Throws<InvalidDataException>(() => new Configuration { NodeName = "test", Transports = [new() { Type = "udp1", Port = 1, Peers = [new() { Key = "key", Port = 1 }] }] }.Validate());
        Assert.Throws<InvalidDataException>(() => new Configuration { NodeName = "test", Transports = [new() { Type = "legacy_tcp_listen", Port = 0, Keys = ["key"] }] }.Validate());
        new Configuration { NodeName = "test" }.Validate();
        new Configuration
        {
            NodeName = "test",
            Transports =
            [
                new() { Type = "udp1", Port = 1, Peers = [new() { Key = "key", Host = "host", Port = 1 }, new() { Key = "key" }] },
                new() { Type = "udp1", Port = 0, Peers = [new() { Key = "key", Host = "host", Port = 1 }] },
            ]
        }.Validate();
    }
}
