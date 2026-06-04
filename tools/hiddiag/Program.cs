using HidSharp;

// Probe VMulti (PID 0xBACC = 47820) exactly like VoiDPlugins' VMultiInstance.Retrieve does,
// plus dump every collection's report lengths so we can see what's actually present.
const int VMULTI_PID = 47820; // 0xBACC

Console.WriteLine("=== All HID devices with PID 0xBACC ===");
var all = DeviceList.Local.GetHidDevices().ToArray();
var vmulti = all.Where(d => { try { return d.ProductID == VMULTI_PID; } catch { return false; } }).ToArray();

if (vmulti.Length == 0)
{
    Console.WriteLine("NONE FOUND with PID 0xBACC. All HID PIDs present:");
    foreach (var d in all)
    {
        try { Console.WriteLine($"  VID=0x{d.VendorID:X4} PID=0x{d.ProductID:X4}  in={d.GetMaxInputReportLength()} out={d.GetMaxOutputReportLength()} feat={d.GetMaxFeatureReportLength()}  {d.GetFriendlyName()}"); }
        catch { }
    }
    return;
}

foreach (var d in vmulti)
{
    int inLen = -1, outLen = -1, featLen = -1;
    try { inLen = d.GetMaxInputReportLength(); } catch { }
    try { outLen = d.GetMaxOutputReportLength(); } catch { }
    try { featLen = d.GetMaxFeatureReportLength(); } catch { }
    string name = "?"; try { name = d.GetFriendlyName(); } catch { }
    Console.WriteLine($"VID=0x{d.VendorID:X4} PID=0x{d.ProductID:X4}  in={inLen} out={outLen} feat={featLen}");
    Console.WriteLine($"   name='{name}'  path={d.DevicePath}");

    // Replicate Retrieve(): looking for a control device with out==65 && in==65
    if (outLen == 65 && inLen == 65)
    {
        Console.Write("   -> matches CONTROL (65/65). TryOpen: ");
        try
        {
            if (d.TryOpen(out var stream)) { Console.WriteLine("OPENED OK"); stream.Dispose(); }
            else Console.WriteLine("TryOpen returned FALSE");
        }
        catch (Exception e) { Console.WriteLine($"EXCEPTION {e.GetType().Name}: {e.Message}"); }
    }

    // digitizer descriptor probe (in==10)
    if (inLen == 10)
    {
        try
        {
            var rd = d.GetReportDescriptor();
            var inputs = rd.Reports.Where(r => r.ReportType == HidSharp.Reports.ReportType.Input).Select(r => r.ReportID);
            Console.WriteLine($"   -> digitizer(in=10) report IDs: {string.Join(",", inputs)}");
        }
        catch (Exception e) { Console.WriteLine($"   -> descriptor read failed: {e.Message}"); }
    }
}
