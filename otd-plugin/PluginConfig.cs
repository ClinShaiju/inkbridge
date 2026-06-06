using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>
    /// Tiny persistent store next to the plugin DLL
    /// (<c>%LOCALAPPDATA%\OpenTabletDriver\Plugins\Inkbridge\inkbridge.json</c>). Holds the paired
    /// device id (so discovery filters to <i>our</i> rMPP) and the last-known Wi-Fi IP (a fast-path
    /// hint while mDNS re-resolves). The pinned device public key will live here too once the identity
    /// handshake lands. JSON so a power user can edit it by hand; all fields optional.
    /// </summary>
    internal sealed class PluginConfig
    {
        public string? device_id { get; set; }   // paired device UUID (TOFU on first discovery)
        public string? wifi_host { get; set; }    // cached last-good Wi-Fi address

        private static readonly object _gate = new();

        private static string Path_ =>
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
                "inkbridge.json");

        public static PluginConfig Load()
        {
            try
            {
                lock (_gate)
                {
                    if (File.Exists(Path_))
                        return JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(Path_)) ?? new PluginConfig();
                }
            }
            catch (Exception e)
            {
                Log.Write("Inkbridge", $"inkbridge.json read failed: {e.Message}", LogLevel.Debug);
            }
            return new PluginConfig();
        }

        public void Save()
        {
            try
            {
                lock (_gate)
                {
                    File.WriteAllText(Path_,
                        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (Exception e)
            {
                Log.Write("Inkbridge", $"inkbridge.json write failed: {e.Message}", LogLevel.Debug);
            }
        }
    }
}
