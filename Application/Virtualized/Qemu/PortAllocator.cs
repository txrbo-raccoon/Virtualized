using System.Net;
using System.Net.Sockets;

namespace Virtualized.Qemu;

public static class PortAllocator
{
    /// <summary>
    /// Asks the OS for a free loopback port. The port is released before QEMU binds it, so another
    /// program can still take it in between; callers retry with new ports if QEMU fails to bind.
    /// </summary>
    public static int GetFreePort(IReadOnlyCollection<int> exclude)
    {
        while (true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (!exclude.Contains(port))
                return port;
        }
    }
}
