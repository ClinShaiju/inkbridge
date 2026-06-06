using System;
using System.Collections.Generic;
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
    /// Pen packet source backed by the shared <see cref="ConnectionManager"/>: the connection,
    /// handshake, encryption, and reconnect all live there now (v2 muxed transport). On open this
    /// subscribes the pen channel; <see cref="Next"/> blocks on the manager's pen-frame queue, which
    /// is fed off channel 1 of the one connection. The OTD device-endpoint contract is unchanged — the
    /// endpoint just no longer owns a socket.
    /// </summary>
    internal sealed class ManagerPenSource : IPacketSource
    {
        public ManagerPenSource() => ConnectionManager.Instance.OpenPen();

        public byte[] Next() => ConnectionManager.Instance.NextPenPacket();

        public void Dispose() => ConnectionManager.Instance.ClosePen();
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
            // The shared ConnectionManager owns the muxed connection (host resolved per-connect by
            // ConnectionConfig); this just subscribes the pen channel and reads decoded frames.
            Log.Write("Inkbridge", "Opening pen packet source (muxed connection)");
            return new InkbridgeStream(new ManagerPenSource());
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
