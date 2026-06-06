using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Devices;

namespace Inkbridge
{
    /// <summary>
    /// Produces 18-byte PenPackets for the endpoint stream: a TCP client to the rMPP daemon.
    /// </summary>
    internal interface IPacketSource : IDisposable
    {
        /// <summary>Blocks until the next 18-byte packet is available.</summary>
        byte[] Next();
    }

    /// <summary>
    /// Connects to the rMPP daemon over USB RNDIS and reads 18-byte frames after the
    /// 4-byte "IBR1" hello. Reconnects on failure. Host/port via env vars.
    /// </summary>
    internal sealed class TcpSource : IPacketSource
    {
        private readonly string _host;
        private readonly int _port;
        private TcpClient? _client;
        private Stream? _stream;
        private bool _disposed;

        public TcpSource(string host, int port) { _host = host; _port = port; }

        private void EnsureConnected()
        {
            while (!_disposed && _stream == null)
            {
                try
                {
                    _client = new TcpClient();
                    _client.NoDelay = true;
                    _client.Connect(_host, _port);
                    var s = _client.GetStream();
                    var hello = new byte[4];
                    ReadExact(s, hello, 4);
                    if (hello[0] != (byte)'I' || hello[1] != (byte)'B' ||
                        hello[2] != (byte)'R' || hello[3] != (byte)'1')
                        throw new IOException("bad inkbridge hello");
                    _stream = s;
                    Log.Write("Inkbridge", $"Connected to daemon at {_host}:{_port}");
                }
                catch (Exception e)
                {
                    Log.Write("Inkbridge", $"Connect to {_host}:{_port} failed: {e.Message}; retrying", LogLevel.Warning);
                    Cleanup();
                    Thread.Sleep(1000);
                }
            }
        }

        public byte[] Next()
        {
            while (true)
            {
                EnsureConnected();
                if (_disposed) return new byte[PenPacket.Size];
                try
                {
                    var buf = new byte[PenPacket.Size];
                    ReadExact(_stream!, buf, PenPacket.Size);
                    InkbridgeTelemetry.NotePacket(); // feed the PC↔device line-rate metric
                    return buf;
                }
                catch (Exception e)
                {
                    Log.Write("Inkbridge", $"Stream read failed: {e.Message}; reconnecting", LogLevel.Warning);
                    Cleanup();
                }
            }
        }

        private static void ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
            }
        }

        private void Cleanup()
        {
            try { _stream?.Dispose(); } catch { }
            try { _client?.Dispose(); } catch { }
            _stream = null; _client = null;
        }

        public void Dispose() { _disposed = true; Cleanup(); }
    }

    /// <summary>OTD device endpoint stream wrapping a packet source.</summary>
    internal sealed class InkbridgeStream : IDeviceEndpointStream
    {
        private readonly IPacketSource _source;
        public InkbridgeStream(IPacketSource source) { _source = source; }

        public byte[] Read() => _source.Next();
        public void Write(byte[] buffer) { }
        public void GetFeature(byte[] buffer) { }
        public void SetFeature(byte[] buffer) { }
        public void Dispose() => _source.Dispose();
    }

    /// <summary>Synthetic HID endpoint OTD matches against tablet-spec.json.</summary>
    internal sealed class InkbridgeEndpoint : IDeviceEndpoint
    {
        // Must match tablet-spec.json DigitizerIdentifiers[0].
        public int VendorID => 0x1209;          // pid.codes open VID
        public int ProductID => 0x4945;         // arbitrary, unique to inkbridge
        public int InputReportLength => PenPacket.Size;
        public int OutputReportLength => 0;
        public int FeatureReportLength => 0;

        public string Manufacturer => "inkbridge";
        public string ProductName => "inkbridge rMPP";
        public string FriendlyName => "inkbridge rMPP";
        public string SerialNumber => "rmpp-0001";
        public string DevicePath => "inkbridge://rmpp";
        public bool CanOpen => true;
        public IDictionary<string, string> DeviceAttributes => new Dictionary<string, string>();

        public IDeviceEndpointStream Open()
        {
            string host = Environment.GetEnvironmentVariable("INKBRIDGE_HOST") ?? "10.11.99.1";
            int port = int.TryParse(Environment.GetEnvironmentVariable("INKBRIDGE_PORT"), out var p) ? p : 9292;
            Log.Write("Inkbridge", $"Opening TCP packet source -> {host}:{port}");
            return new InkbridgeStream(new TcpSource(host, port));
        }

        public string GetDeviceString(byte index) => string.Empty;
    }

    /// <summary>A device hub exposing the single inkbridge endpoint.</summary>
    internal sealed class InkbridgeHub : IDeviceHub
    {
        // The endpoint is static (always present once the hub is connected), so this
        // event never fires — but the interface requires it.
#pragma warning disable CS0067
        public event EventHandler<DevicesChangedEventArgs>? DevicesChanged;
#pragma warning restore CS0067
        private readonly InkbridgeEndpoint _endpoint = new();

        public IEnumerable<IDeviceEndpoint> GetDevices()
        {
            yield return _endpoint;
        }
    }
}
