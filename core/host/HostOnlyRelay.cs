// The core's screen on this PC's IPv4 address, for this PC only (Windows PowerShell compiles this with Add-Type,
// so it is written in C# 5: nothing to install). It listens on the address, accepts a connection only when it comes
// from one of this PC's own addresses (or loopback), and relays its bytes to the core's gateway on 127.0.0.1.
// A connection from any other PC is closed at once, before a byte is read. The relay never reads what it carries:
// the core still asks for the one-time password, and the gateway still refuses unknown host names.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace Robomori
{
    public sealed class HostOnlyRelay
    {
        private const int MaxOpen = 64;           // a browser opens a handful; more than this is not a browser
        private static readonly TimeSpan OwnRefresh = TimeSpan.FromSeconds(10);
        private readonly TcpListener listener;
        private readonly int upstreamPort;
        private readonly object gate = new object();
        private volatile bool running;
        private HashSet<string> own = new HashSet<string>();
        private DateTime ownAt = DateTime.MinValue;
        private int served, refused, open;
        private string lastRefused = "";

        /// <summary>Who may connect. Defaults to this PC's own addresses and loopback (replaceable for tests).</summary>
        public Func<IPAddress, bool> Allow;
        public int Served { get { return served; } }
        public int Refused { get { return refused; } }
        public string LastRefused { get { lock (gate) { return lastRefused; } } }

        private HostOnlyRelay(IPAddress address, int port, int upstream)
        {
            listener = new TcpListener(address, port);
            listener.ExclusiveAddressUse = true;
            upstreamPort = upstream;
            Allow = IsOwn;
        }

        /// <summary>Starts listening on an IPv4 address of this PC; throws when the address or port cannot be used.</summary>
        public static HostOnlyRelay Start(string address, int port, int upstreamPort)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(address, out ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("an IPv4 address of this PC is needed: " + address);
            if (port <= 0 || port > 65535 || upstreamPort <= 0 || upstreamPort > 65535) throw new ArgumentException("port out of range");
            var relay = new HostOnlyRelay(ip, port, upstreamPort);
            relay.listener.Start(32);
            relay.running = true;
            var thread = new Thread(relay.AcceptLoop);
            thread.IsBackground = true;
            thread.Name = "Robomori host-only relay";
            thread.Start();
            return relay;
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); } catch (SocketException) { }
        }

        private bool IsOwn(IPAddress remote)
        {
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            if (IPAddress.IsLoopback(remote)) return true;
            lock (gate)
            {
                if (DateTime.UtcNow - ownAt > OwnRefresh) { own = OwnAddresses(); ownAt = DateTime.UtcNow; }
                return own.Contains(remote.ToString());
            }
        }

        private static HashSet<string> OwnAddresses()
        {
            var set = new HashSet<string>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (UnicastIPAddressInformation a in nic.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork) set.Add(a.Address.ToString());
                }
            }
            catch (NetworkInformationException) { }
            return set;
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { if (!running) return; continue; }
                catch (ObjectDisposedException) { return; }
                IPAddress remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                bool allowed;
                try { allowed = Allow(remote); } catch (Exception) { allowed = false; }
                if (!allowed || Interlocked.CompareExchange(ref open, 0, 0) >= MaxOpen)
                {
                    Interlocked.Increment(ref refused);
                    lock (gate) { lastRefused = remote.ToString(); }
                    try { client.Client.LingerState = new LingerOption(true, 0); } catch (SocketException) { }
                    Close(client);   // cut off before reading anything
                    continue;
                }
                var thread = new Thread(Serve);
                thread.IsBackground = true;
                thread.Start(client);
            }
        }

        private void Serve(object state)
        {
            var client = (TcpClient)state;
            TcpClient upstream = null;
            Interlocked.Increment(ref open);
            try
            {
                upstream = new TcpClient();
                upstream.Connect(IPAddress.Loopback, upstreamPort);
                client.NoDelay = true;
                upstream.NoDelay = true;
                Interlocked.Increment(ref served);
                NetworkStream down = client.GetStream(), up = upstream.GetStream();
                TcpClient a = client, b = upstream;
                var back = new Thread(() => Pump(up, down, a, b));
                back.IsBackground = true;
                back.Start();
                Pump(down, up, a, b);
                back.Join(5000);
            }
            catch (Exception) { }
            finally
            {
                Close(client);
                Close(upstream);
                Interlocked.Decrement(ref open);
            }
        }

        private static void Pump(NetworkStream from, NetworkStream to, TcpClient one, TcpClient other)
        {
            var buffer = new byte[16384];
            try
            {
                int n;
                while ((n = from.Read(buffer, 0, buffer.Length)) > 0) to.Write(buffer, 0, n);
            }
            catch (Exception) { }
            // Either side ended: end both, so the other direction stops too.
            Close(one);
            Close(other);
        }

        private static void Close(TcpClient client)
        {
            if (client == null) return;
            try { client.Close(); } catch (Exception) { }
        }
    }
}
