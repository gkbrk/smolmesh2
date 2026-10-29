using System.Buffers.Binary;
using System.Text;
using Smolmesh;
using Xunit;

public class NativeTests
{
    [Fact]
    public void MacOsControlSocketAbi()
    {
        // Does not create an adapter; only exercises the native ABI on macOS.
        if (!OperatingSystem.IsMacOS()) return;
        int fd = UnixTun.Native.socket(32, 2, 2);
        Assert.True(fd >= 0, "Cannot open control socket.");
        try
        {
            byte[] info = new byte[100];
            Encoding.ASCII.GetBytes("com.apple.net.utun_control").CopyTo(info, 4);
            Assert.Equal(0, UnixTun.Native.ioctl(fd, 0xc0644e03, info));
            Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(info));
            int flags = UnixTun.Native.fcntl(fd, 3, 0);
            Assert.True(flags >= 0);
            Assert.Equal(0, UnixTun.Native.fcntl(fd, 4, flags | 4));
            Assert.True((UnixTun.Native.fcntl(fd, 3, 0) & 4) != 0);
        }
        finally { UnixTun.Native.close(fd); }
    }
}
