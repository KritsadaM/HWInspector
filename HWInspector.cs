// HW Inspector - hardware information viewer (CPU-Z / dxdiag style)
// Build: see build.cmd  (uses the C# compiler that ships with .NET Framework 4)
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("HW Inspector")]
[assembly: System.Reflection.AssemblyProduct("HW Inspector")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyCompany("HW Inspector")]
[assembly: System.Reflection.AssemblyDescription("Hardware information and 3D mainboard viewer")]

namespace HwInspector
{
    // ------------------------------------------------------------------ model
    class InfoGroup
    {
        public string Name;
        public List<string[]> Rows = new List<string[]>();
        public void Add(string key, string value)
        {
            if (!string.IsNullOrEmpty(value)) Rows.Add(new[] { key, value });
        }
    }

    class InfoPage
    {
        public string Title;
        public List<InfoGroup> Groups = new List<InfoGroup>();
        public InfoGroup G(string name)
        {
            var g = new InfoGroup { Name = name };
            Groups.Add(g);
            return g;
        }
    }

    class DeviceInfo
    {
        public string Name, Class, Manufacturer, Status, DeviceId, Bus, Service, HardwareIds;
        public int ErrorCode;
        public bool Virtual;
        public int PciBus = -1, PciDev = -1, PciFn = -1;
        public string ParentId = "";
    }

    class Snapshot
    {
        public List<InfoPage> Pages = new List<InfoPage>();
        public List<DeviceInfo> Devices = new List<DeviceInfo>();
        public List<string> Errors = new List<string>();
        public DateTime Time;
        public TimeSpan Duration;
        public BoardLayout Board;
        public string ComputerName = Environment.MachineName;
    }

    // -------------------------------------------------------------- helpers
    static class H
    {
        public static string S(ManagementBaseObject o, string p)
        {
            try
            {
                object v = o[p];
                if (v == null) return "";
                var arr = v as Array;
                if (arr != null)
                {
                    var parts = new List<string>();
                    foreach (var x in arr) if (x != null) parts.Add(x.ToString().Trim());
                    return string.Join(", ", parts);
                }
                return v.ToString().Trim();
            }
            catch { return ""; }
        }

        public static ulong U(ManagementBaseObject o, string p)
        {
            ulong r;
            return ulong.TryParse(S(o, p), out r) ? r : 0;
        }

        public static long L(ManagementBaseObject o, string p)
        {
            long r;
            return long.TryParse(S(o, p), out r) ? r : -1;
        }

        public static string Bytes(double b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return b.ToString(i == 0 ? "0" : "0.##") + " " + u[i];
        }

        public static string DecimalSize(double b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
            int i = 0;
            while (b >= 1000 && i < u.Length - 1) { b /= 1000; i++; }
            return b.ToString(i == 0 ? "0" : "0.#") + " " + u[i];
        }

        public static string Dmtf(string s, string fmt)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return ManagementDateTimeConverter.ToDateTime(s).ToString(fmt); }
            catch { return s; }
        }

        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string l = s.ToLowerInvariant();
            if (l == "default string" || l.Contains("to be filled") || l == "system serial number" ||
                l == "system product name" || l == "system manufacturer" || l == "not applicable" ||
                l == "none" || l == "0" || l == "base board serial number" || l == "x.x" || l == "n/a")
                return s + "  (OEM placeholder)";
            return s;
        }

        public static string Pick(Dictionary<int, string> map, long key)
        {
            string v;
            if (map.TryGetValue((int)key, out v)) return v;
            return key < 0 ? "" : "Code " + key;
        }

        public static object Reg(string path, string name)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(path))
                    return k == null ? null : k.GetValue(name);
            }
            catch { return null; }
        }

        public static ulong ObjToULong(object v)
        {
            if (v == null) return 0;
            if (v is long) return (ulong)(long)v;
            if (v is int) return (ulong)(uint)(int)v;
            var b = v as byte[];
            if (b != null)
            {
                if (b.Length >= 8) return BitConverter.ToUInt64(b, 0);
                if (b.Length >= 4) return BitConverter.ToUInt32(b, 0);
            }
            return 0;
        }

        public static string WmiChars(ManagementBaseObject o, string p)
        {
            try
            {
                var arr = o[p] as Array;
                if (arr == null) return "";
                var sb = new StringBuilder();
                foreach (var x in arr)
                {
                    int c = Convert.ToInt32(x);
                    if (c == 0) break;
                    sb.Append((char)c);
                }
                return sb.ToString().Trim();
            }
            catch { return ""; }
        }

        static readonly Dictionary<string, string> PciVendors = new Dictionary<string, string>
        {
            {"8086","Intel"},{"8087","Intel"},{"1022","AMD"},{"1002","AMD/ATI"},{"10DE","NVIDIA"},
            {"10EC","Realtek"},{"0BDA","Realtek"},{"14E4","Broadcom"},{"0A5C","Broadcom"},{"1B21","ASMedia"},
            {"144D","Samsung"},{"04E8","Samsung"},{"1987","Phison"},{"15B7","SanDisk/WD"},{"1CC1","ADATA"},
            {"2646","Kingston"},{"0951","Kingston"},{"C0A9","Micron/Crucial"},{"1344","Micron"},{"1179","Toshiba/Kioxia"},
            {"1E0F","Kioxia"},{"168C","Qualcomm Atheros"},{"17CB","Qualcomm"},{"1969","Qualcomm Atheros"},
            {"106B","Apple"},{"05AC","Apple"},{"1414","Microsoft"},{"045E","Microsoft"},{"15AD","VMware"},
            {"80EE","VirtualBox"},{"1AF4","Red Hat VirtIO"},{"1B4B","Marvell"},{"11AB","Marvell"},{"1D6A","Aquantia"},
            {"046D","Logitech"},{"1532","Razer"},{"1B1C","Corsair"},{"0B05","ASUS"},{"1043","ASUS"},{"1462","MSI"},
            {"1458","Gigabyte"},{"1849","ASRock"},{"17AA","Lenovo"},{"103C","HP"},{"03F0","HP"},{"1028","Dell"},
            {"413C","Dell"},{"1025","Acer"},{"8564","Transcend"},{"0781","SanDisk"},{"058F","Alcor Micro"},
            {"05E3","Genesys Logic"},{"2109","VIA Labs"},{"1106","VIA"},{"1912","Renesas"},{"1033","NEC/Renesas"},
            {"0E8D","MediaTek"},{"14C3","MediaTek"},{"13D3","IMC Networks"},{"0489","Foxconn"},
            {"04F2","Chicony"},{"5986","Acer/Bison"},{"0C45","Microdia"},{"258A","SINO WEALTH"},
            {"3297","ZSA"},{"054C","Sony"},{"057E","Nintendo"},{"28DE","Valve"},{"1050","Yubico"},{"1D5C","Fresco Logic"},
            {"1C5C","SK hynix"},{"126F","Silicon Motion"},{"1E4B","Maxio"},{"1D97","Shenzhen Longsys"}
        };

        public static string Vendor(string id)
        {
            string v;
            return PciVendors.TryGetValue(id.ToUpperInvariant(), out v) ? v : "";
        }

        public static string Ids(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return "";
            var m = Regex.Match(deviceId, @"VEN_([0-9A-Fa-f]{4})&DEV_([0-9A-Fa-f]{4})");
            if (!m.Success) m = Regex.Match(deviceId, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
            if (!m.Success) return "";
            string ven = m.Groups[1].Value.ToUpperInvariant(), dev = m.Groups[2].Value.ToUpperInvariant();
            string name = Vendor(ven);
            return ven + ":" + dev + (name != "" ? "  (" + name + ")" : "");
        }

        public static string ErrorText(int code)
        {
            switch (code)
            {
                case 0: return "Working properly";
                case 1: return "Not configured correctly (1)";
                case 3: return "Driver may be corrupted (3)";
                case 10: return "Cannot start (10)";
                case 12: return "Not enough free resources (12)";
                case 14: return "Restart required (14)";
                case 18: return "Reinstall drivers (18)";
                case 19: return "Registry configuration incomplete/damaged (19)";
                case 21: return "Being removed (21)";
                case 22: return "Disabled (22)";
                case 24: return "Not present / not working (24)";
                case 28: return "Drivers not installed (28)";
                case 29: return "Disabled by firmware (29)";
                case 31: return "Not working properly - driver load failed (31)";
                case 37: return "Driver failed to initialize (37)";
                case 39: return "Driver corrupted or missing (39)";
                case 43: return "Stopped - device reported problems (43)";
                case 45: return "Not connected (45)";
                case 52: return "Driver signature cannot be verified (52)";
                default: return "Problem code " + code;
            }
        }

        static readonly Dictionary<string, string> ClassNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"System","System devices"},{"Net","Network adapters"},{"DiskDrive","Disk drives"},
            {"Display","Display adapters"},{"Monitor","Monitors"},{"HIDClass","Human Interface Devices"},
            {"USB","Universal Serial Bus controllers"},{"USBDevice","Universal Serial Bus devices"},
            {"MEDIA","Sound, video and game controllers"},{"AudioEndpoint","Audio inputs and outputs"},
            {"Processor","Processors"},{"Keyboard","Keyboards"},{"Mouse","Mice and pointing devices"},
            {"SCSIAdapter","Storage controllers"},{"HDC","IDE ATA/ATAPI controllers"},{"Bluetooth","Bluetooth"},
            {"Camera","Cameras"},{"Image","Imaging devices"},{"Battery","Batteries"},{"Firmware","Firmware"},
            {"SecurityDevices","Security devices"},{"SoftwareDevice","Software devices"},{"Ports","Ports (COM & LPT)"},
            {"PrintQueue","Print queues"},{"Printer","Printers"},{"Volume","Storage volumes"},{"Computer","Computer"},
            {"Biometric","Biometric devices"},{"SmartCardReader","Smart card readers"},{"WPD","Portable devices"},
            {"Extension","Extensions"},{"SoftwareComponent","Software components"},{"CDROM","DVD/CD-ROM drives"},
            {"Sensor","Sensors"},{"MTD","Memory technology devices"},{"SDHost","SD host adapters"},
            {"VolumeSnapshot","Storage volume shadow copies"},{"LegacyDriver","Non-Plug and Play drivers"},
            {"Infrared","Infrared devices"},{"Modem","Modems"},{"NetService","Network services"}
        };

        public static string ClassName(string cls)
        {
            string v;
            return ClassNames.TryGetValue(cls, out v) ? v : cls;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GetFirmwareType(out int type);

        [DllImport("kernel32.dll")]
        public static extern bool IsProcessorFeaturePresent(uint feature);
    }

    // ------------------------------------------------------------ collector
    class Collector
    {
        readonly Snapshot snap = new Snapshot();
        readonly BoardData bd = new BoardData();
        string sumCpu = "", sumBoard = "", sumBios = "", sumRam = "", sumOs = "", sumModel = "";
        readonly List<string> sumGpu = new List<string>(), sumDisk = new List<string>(),
                              sumMon = new List<string>(), sumNet = new List<string>();

        static string S(ManagementBaseObject o, string p) { return H.S(o, p); }
        static ulong U(ManagementBaseObject o, string p) { return H.U(o, p); }
        static long L(ManagementBaseObject o, string p) { return H.L(o, p); }

        List<ManagementObject> Q(string wql) { return Q(@"root\cimv2", wql); }

        List<ManagementObject> Q(string ns, string wql)
        {
            var list = new List<ManagementObject>();
            try
            {
                var opt = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(30), ReturnImmediately = true };
                using (var s = new ManagementObjectSearcher(new ManagementScope(ns), new ObjectQuery(wql), opt))
                    foreach (ManagementObject o in s.Get()) list.Add(o);
            }
            catch (Exception ex)
            {
                snap.Errors.Add(ns + " | " + wql + " -> " + ex.Message.Trim());
            }
            return list;
        }

        void Safe(string title, Func<InfoPage> f, List<InfoPage> pages)
        {
            try { pages.Add(f()); }
            catch (Exception ex)
            {
                var p = new InfoPage { Title = title };
                p.G("Error").Add("Collector failed", ex.Message);
                pages.Add(p);
            }
        }

        public Snapshot Run(Action<string> progress)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            snap.Time = DateTime.Now;
            var pages = new List<InfoPage>();
            progress("Enumerating Plug and Play devices...");
            CollectDevices();
            progress("System / OS..."); Safe("System", SystemPage, pages);
            progress("Mainboard / BIOS..."); Safe("Mainboard", BoardPage, pages);
            progress("Processor..."); Safe("CPU", CpuPage, pages);
            progress("Memory..."); Safe("Memory", MemoryPage, pages);
            progress("Graphics..."); Safe("Graphics", GpuPage, pages);
            progress("Monitors..."); Safe("Monitors", MonitorPage, pages);
            progress("Storage..."); Safe("Storage", StoragePage, pages);
            progress("Network..."); Safe("Network", NetworkPage, pages);
            progress("Audio..."); Safe("Audio", AudioPage, pages);
            progress("USB..."); Safe("USB", UsbPage, pages);
            progress("Peripherals..."); Safe("Peripherals", PeripheralPage, pages);
            progress("Building board model...");
            try { snap.Board = BoardBuilder.Build(bd, snap, pages); }
            catch (Exception ex) { snap.Errors.Add("Board view: " + ex.Message); }
            snap.Pages.Add(SummaryPage());
            snap.Pages.AddRange(pages);
            snap.Duration = sw.Elapsed;
            return snap;
        }

        // ---------------- devices
        static readonly HashSet<string> VirtualBuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ROOT", "SWD", "SW", "HTREE", "STORAGE", "UMB", "ACPI_HAL", "VMBUS", "TS_USB_HUB", "RDPBUS" };
        static readonly HashSet<string> VirtualClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SoftwareDevice", "SoftwareComponent", "Volume", "VolumeSnapshot", "LegacyDriver", "PrintQueue", "Extension", "NetService" };

        void CollectDevices()
        {
            foreach (var o in Q("SELECT * FROM Win32_PnPEntity"))
            {
                var d = new DeviceInfo();
                d.Name = S(o, "Name");
                if (d.Name == "") d.Name = S(o, "Description");
                if (d.Name == "") d.Name = "(Unknown device)";
                d.Class = S(o, "PNPClass");
                if (d.Class == "") d.Class = "Other";
                d.Manufacturer = S(o, "Manufacturer");
                d.Status = S(o, "Status");
                d.Service = S(o, "Service");
                d.DeviceId = S(o, "PNPDeviceID");
                d.HardwareIds = S(o, "HardwareID");
                long ec = L(o, "ConfigManagerErrorCode");
                d.ErrorCode = ec < 0 ? 0 : (int)ec;
                int slash = d.DeviceId.IndexOf('\\');
                d.Bus = slash > 0 ? d.DeviceId.Substring(0, slash) : d.DeviceId;
                d.Virtual = VirtualBuses.Contains(d.Bus) || VirtualClasses.Contains(d.Class) || d.Bus.StartsWith("{");
                if (d.Bus.Equals("PCI", StringComparison.OrdinalIgnoreCase)) Native.PciLocation(d);
                d.ParentId = Native.Parent(d.DeviceId);
                snap.Devices.Add(d);
            }
        }

        IEnumerable<DeviceInfo> Real(string cls)
        {
            return snap.Devices.Where(d => !d.Virtual && string.Equals(d.Class, cls, StringComparison.OrdinalIgnoreCase));
        }

        static void AddDeviceList(InfoGroup g, IEnumerable<DeviceInfo> devs)
        {
            foreach (var grp in devs.GroupBy(d => d.Name).OrderBy(x => x.Key))
            {
                var first = grp.First();
                string val = H.Ids(first.DeviceId);
                if (val == "") val = first.Manufacturer;
                if (grp.Count() > 1) val = "x" + grp.Count() + (val != "" ? "  ·  " + val : "");
                int err = grp.Max(d => d.ErrorCode);
                if (err != 0) val = "[!] " + H.ErrorText(err) + "  ·  " + val;
                if (val == "") val = first.Status;
                g.Add(grp.Key, val);
            }
        }

        // ---------------- pages
        InfoPage SummaryPage()
        {
            var p = new InfoPage { Title = "Summary" };
            var g = p.G("Overview");
            g.Add("Computer", sumModel);
            g.Add("Operating System", sumOs);
            g.Add("Mainboard", sumBoard);
            g.Add("BIOS / UEFI", sumBios);
            g.Add("Processor", sumCpu);
            g.Add("Memory", sumRam);
            for (int i = 0; i < sumGpu.Count; i++) g.Add(sumGpu.Count > 1 ? "Graphics #" + (i + 1) : "Graphics", sumGpu[i]);
            for (int i = 0; i < sumMon.Count; i++) g.Add(sumMon.Count > 1 ? "Monitor #" + (i + 1) : "Monitor", sumMon[i]);
            for (int i = 0; i < sumDisk.Count; i++) g.Add("Storage #" + (i + 1), sumDisk[i]);
            for (int i = 0; i < sumNet.Count; i++) g.Add(sumNet.Count > 1 ? "Network #" + (i + 1) : "Network", sumNet[i]);

            var h = p.G("Device health");
            int real = snap.Devices.Count(d => !d.Virtual);
            h.Add("Devices detected", snap.Devices.Count + " total  (" + real + " hardware, " + (snap.Devices.Count - real) + " software/virtual)");
            var bad = snap.Devices.Where(d => d.ErrorCode != 0).ToList();
            h.Add("Devices with problems", bad.Count == 0 ? "None - all devices working properly" : bad.Count.ToString());
            foreach (var d in bad) h.Add("  [!] " + d.Name, H.ErrorText(d.ErrorCode));

            if (snap.Errors.Count > 0)
            {
                var e = p.G("Scan notes (some WMI queries were not available)");
                foreach (var s in snap.Errors) e.Add("note", s);
            }
            return p;
        }

        static readonly Dictionary<int, string> PcTypes = new Dictionary<int, string>
        { {0,"Unspecified"},{1,"Desktop"},{2,"Mobile / Laptop"},{3,"Workstation"},{4,"Enterprise Server"},{5,"SOHO Server"},{6,"Appliance PC"},{7,"Performance Server"},{8,"Slate / Tablet"} };
        static readonly Dictionary<int, string> Chassis = new Dictionary<int, string>
        { {1,"Other"},{2,"Unknown"},{3,"Desktop"},{4,"Low Profile Desktop"},{5,"Pizza Box"},{6,"Mini Tower"},{7,"Tower"},{8,"Portable"},{9,"Laptop"},{10,"Notebook"},{11,"Hand Held"},{12,"Docking Station"},{13,"All in One"},{14,"Sub Notebook"},{15,"Space-Saving"},{16,"Lunch Box"},{17,"Main System Chassis"},{23,"Rack Mount"},{24,"Sealed-Case PC"},{30,"Tablet"},{31,"Convertible"},{32,"Detachable"},{33,"IoT Gateway"},{34,"Embedded PC"},{35,"Mini PC"},{36,"Stick PC"} };

        InfoPage SystemPage()
        {
            var p = new InfoPage { Title = "System" };
            foreach (var o in Q("SELECT * FROM Win32_ComputerSystem"))
            {
                var g = p.G("Computer");
                g.Add("Computer Name", S(o, "Name"));
                g.Add("Manufacturer", H.Clean(S(o, "Manufacturer")));
                g.Add("Model", H.Clean(S(o, "Model")));
                g.Add("System Family", H.Clean(S(o, "SystemFamily")));
                g.Add("SKU", H.Clean(S(o, "SystemSKUNumber")));
                g.Add("PC Type", H.Pick(PcTypes, L(o, "PCSystemType")));
                g.Add("System Type", S(o, "SystemType"));
                g.Add("Domain / Workgroup", S(o, "Domain"));
                g.Add("Physical Processors", S(o, "NumberOfProcessors"));
                g.Add("Logical Processors", S(o, "NumberOfLogicalProcessors"));
                g.Add("Total Visible Memory", H.Bytes(U(o, "TotalPhysicalMemory")));
                g.Add("Hypervisor Present", S(o, "HypervisorPresent"));
                sumModel = (S(o, "Manufacturer") + " " + S(o, "Model")).Trim();
            }
            foreach (var o in Q("SELECT * FROM Win32_SystemEnclosure"))
            {
                var g = p.G("Chassis");
                try
                {
                    var types = o["ChassisTypes"] as ushort[];
                    if (types != null && types.Length > 0) g.Add("Chassis Type", H.Pick(Chassis, types[0]));
                }
                catch { }
                g.Add("Manufacturer", H.Clean(S(o, "Manufacturer")));
                g.Add("Serial Number", H.Clean(S(o, "SerialNumber")));
                g.Add("Asset Tag", H.Clean(S(o, "SMBIOSAssetTag")));
            }
            foreach (var o in Q("SELECT * FROM Win32_OperatingSystem"))
            {
                var g = p.G("Operating System");
                string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
                string disp = Convert.ToString(H.Reg(cv, "DisplayVersion"));
                object ubr = H.Reg(cv, "UBR");
                string build = S(o, "BuildNumber") + (ubr != null ? "." + ubr : "");
                g.Add("Name", S(o, "Caption"));
                g.Add("Version", disp);
                g.Add("Build", build);
                g.Add("Architecture", S(o, "OSArchitecture"));
                g.Add("Language (LCID)", S(o, "OSLanguage"));
                g.Add("Install Date", H.Dmtf(S(o, "InstallDate"), "yyyy-MM-dd HH:mm"));
                string boot = S(o, "LastBootUpTime");
                g.Add("Last Boot", H.Dmtf(boot, "yyyy-MM-dd HH:mm"));
                try
                {
                    var up = DateTime.Now - ManagementDateTimeConverter.ToDateTime(boot);
                    g.Add("Uptime", string.Format("{0}d {1}h {2}m", up.Days, up.Hours, up.Minutes));
                }
                catch { }
                g.Add("System Drive", S(o, "SystemDrive"));
                g.Add("Windows Directory", S(o, "WindowsDirectory"));
                sumOs = (S(o, "Caption") + " " + disp + " (build " + build + ") " + S(o, "OSArchitecture")).Trim();
            }
            var f = p.G("Firmware & Security");
            object fw = H.Reg(@"SYSTEM\CurrentControlSet\Control", "PEFirmwareType");
            int fwt = 0;
            try { H.GetFirmwareType(out fwt); } catch { }
            if (fwt == 0 && fw != null) fwt = Convert.ToInt32(fw);
            if (fwt == 1 || fwt == 2) f.Add("Firmware Type", fwt == 2 ? "UEFI" : "Legacy BIOS");
            object sb = H.Reg(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
            f.Add("Secure Boot", sb == null ? "Not supported / unknown" : (Convert.ToInt32(sb) == 1 ? "Enabled" : "Disabled"));
            var tpm = snap.Devices.FirstOrDefault(d => d.Class.Equals("SecurityDevices", StringComparison.OrdinalIgnoreCase) && d.Name.IndexOf("Trusted Platform", StringComparison.OrdinalIgnoreCase) >= 0);
            f.Add("TPM", tpm != null ? tpm.Name : "Not detected");
            return p;
        }

        static readonly Dictionary<int, string> SlotUsage = new Dictionary<int, string> { {1,"Other"},{2,"Unknown"},{3,"Available"},{4,"In use"} };
        static readonly Dictionary<int, string> SlotWidth = new Dictionary<int, string>
        { {0,"8-bit"},{1,"16-bit"},{2,"32-bit"},{3,"64-bit"},{4,"128-bit"},{5,"x1"},{6,"x2"},{7,"x4"},{8,"x8"},{9,"x12"},{10,"x16"},{11,"x32"} };
        static readonly Dictionary<int, string> PortTypes = new Dictionary<int, string>
        { {0,"None"},{1,"Parallel"},{2,"Parallel PS/2"},{3,"Parallel ECP"},{4,"Parallel EPP"},{5,"Parallel ECP/EPP"},{6,"Serial"},{7,"Serial 16450"},{8,"Serial 16550"},{9,"Serial 16550A"},{10,"SCSI"},{11,"MIDI"},{12,"Joystick"},{13,"Keyboard"},{14,"Mouse"},{15,"SSA SCSI"},{16,"USB"},{17,"FireWire"},{18,"PCMCIA I"},{19,"PCMCIA II"},{20,"PCMCIA III"},{21,"CardBus"},{22,"Access Bus"},{23,"SCSI II"},{24,"SCSI Wide"},{28,"Video"},{29,"Audio"},{30,"Modem"},{31,"Network"},{32,"SATA"},{33,"SAS"},{255,"Other"} };

        InfoPage BoardPage()
        {
            var p = new InfoPage { Title = "Mainboard" };
            foreach (var o in Q("SELECT * FROM Win32_BaseBoard"))
            {
                var g = p.G("Motherboard");
                g.Add("Manufacturer", H.Clean(S(o, "Manufacturer")));
                g.Add("Model", H.Clean(S(o, "Product")));
                g.Add("Revision", H.Clean(S(o, "Version")));
                g.Add("Serial Number", H.Clean(S(o, "SerialNumber")));
                g.Add("Hosting Board", S(o, "HostingBoard"));
                g.Add("Replaceable", S(o, "Replaceable"));
                sumBoard = (S(o, "Manufacturer") + " " + S(o, "Product") + " " + S(o, "Version")).Trim();
                bd.Manufacturer = S(o, "Manufacturer");
                bd.Product = S(o, "Product");
            }
            var lpc = snap.Devices.FirstOrDefault(d => d.Bus == "PCI" && Regex.IsMatch(d.Name, @"LPC|eSPI|ISA bridge|Chipset", RegexOptions.IgnoreCase));
            var host = snap.Devices.FirstOrDefault(d => d.Bus == "PCI" && Regex.IsMatch(d.Name, @"Host Bridge|Root Complex|host CPU bridge|DRAM", RegexOptions.IgnoreCase));
            if (lpc != null || host != null)
            {
                var c = p.G("Chipset");
                if (host != null) c.Add("Host bridge / North", host.Name + "  ·  " + H.Ids(host.DeviceId));
                if (lpc != null) c.Add("LPC / South bridge", lpc.Name + "  ·  " + H.Ids(lpc.DeviceId));
            }
            foreach (var o in Q("SELECT * FROM Win32_BIOS"))
            {
                var g = p.G("BIOS / UEFI");
                g.Add("Vendor", S(o, "Manufacturer"));
                g.Add("Version", S(o, "SMBIOSBIOSVersion"));
                g.Add("Release Date", H.Dmtf(S(o, "ReleaseDate"), "yyyy-MM-dd"));
                string maj = S(o, "SystemBiosMajorVersion"), min = S(o, "SystemBiosMinorVersion");
                if (maj != "" && maj != "255") g.Add("BIOS Revision", maj + "." + min);
                g.Add("SMBIOS Version", S(o, "SMBIOSMajorVersion") + "." + S(o, "SMBIOSMinorVersion"));
                g.Add("Embedded Controller", S(o, "EmbeddedControllerMajorVersion") == "255" ? "" : S(o, "EmbeddedControllerMajorVersion") + "." + S(o, "EmbeddedControllerMinorVersion"));
                g.Add("BIOS Serial", H.Clean(S(o, "SerialNumber")));
                sumBios = (S(o, "Manufacturer") + " " + S(o, "SMBIOSBIOSVersion") + " (" + H.Dmtf(S(o, "ReleaseDate"), "yyyy-MM-dd") + ")").Trim();
            }
            foreach (var o in Q("SELECT * FROM Win32_ComputerSystemProduct"))
            {
                var g = p.G("System Product (SMBIOS)");
                g.Add("Vendor", H.Clean(S(o, "Vendor")));
                g.Add("Name", H.Clean(S(o, "Name")));
                g.Add("Version", H.Clean(S(o, "Version")));
                g.Add("Identifying Number", H.Clean(S(o, "IdentifyingNumber")));
                g.Add("UUID", S(o, "UUID"));
            }
            try { bd.Slots = Native.ReadSlots(); }
            catch (Exception ex) { snap.Errors.Add("Raw SMBIOS slots: " + ex.Message); }
            var slots = Q("SELECT * FROM Win32_SystemSlot");
            if (slots.Count > 0)
            {
                var g = p.G("Expansion slots (" + slots.Count + ")");
                foreach (var o in slots)
                {
                    string width = H.Pick(SlotWidth, L(o, "MaxDataWidth"));
                    g.Add(S(o, "SlotDesignation"), H.Pick(SlotUsage, L(o, "CurrentUsage")) + (width != "" ? "  ·  " + width : ""));
                }
            }
            var ports = Q("SELECT * FROM Win32_PortConnector");
            if (ports.Count > 0)
            {
                var g = p.G("On-board connectors (" + ports.Count + ")");
                foreach (var o in ports)
                {
                    string ext = S(o, "ExternalReferenceDesignator"), inn = S(o, "InternalReferenceDesignator");
                    string name = ext != "" ? ext : inn;
                    if (ext != "" && inn != "" && inn != ext) name = inn + " / " + ext;
                    g.Add(name == "" ? "(unnamed)" : name, H.Pick(PortTypes, L(o, "PortType")));
                    bd.Connectors.Add(new Connector { Internal = inn, External = ext, PortType = (int)Math.Max(0, L(o, "PortType")) });
                }
            }
            var pci = snap.Devices.Where(d => d.Bus.Equals("PCI", StringComparison.OrdinalIgnoreCase)).OrderBy(d => d.Class).ThenBy(d => d.Name).ToList();
            if (pci.Count > 0)
            {
                var g = p.G("PCI / PCIe devices on the board (" + pci.Count + ")");
                foreach (var d in pci)
                    g.Add(d.Name, (d.ErrorCode != 0 ? "[!] " + H.ErrorText(d.ErrorCode) + "  ·  " : "") + H.ClassName(d.Class) + "  ·  " + H.Ids(d.DeviceId));
            }
            return p;
        }

        static readonly Dictionary<int, string> Arch = new Dictionary<int, string> { {0,"x86"},{1,"MIPS"},{2,"Alpha"},{3,"PowerPC"},{5,"ARM"},{6,"ia64"},{9,"x64"},{12,"ARM64"} };

        InfoPage CpuPage()
        {
            var p = new InfoPage { Title = "CPU" };
            int n = 0;
            foreach (var o in Q("SELECT * FROM Win32_Processor"))
            {
                n++;
                var g = p.G("Processor #" + n);
                g.Add("Name", S(o, "Name"));
                g.Add("Manufacturer", S(o, "Manufacturer"));
                g.Add("Identifier", S(o, "Description"));
                g.Add("Processor ID", S(o, "ProcessorId"));
                g.Add("Socket", S(o, "SocketDesignation"));
                g.Add("Architecture", H.Pick(Arch, L(o, "Architecture")));
                g.Add("Cores", S(o, "NumberOfCores") + (S(o, "NumberOfEnabledCore") != "" ? "  (" + S(o, "NumberOfEnabledCore") + " enabled)" : ""));
                g.Add("Threads", S(o, "ThreadCount") != "" ? S(o, "ThreadCount") : S(o, "NumberOfLogicalProcessors"));
                g.Add("Base / Max Clock", S(o, "MaxClockSpeed") + " MHz");
                g.Add("Current Clock", S(o, "CurrentClockSpeed") + " MHz");
                g.Add("External Clock (Bus)", S(o, "ExtClock") != "" ? S(o, "ExtClock") + " MHz" : "");
                ulong l2 = U(o, "L2CacheSize"), l3 = U(o, "L3CacheSize");
                if (l2 > 0) g.Add("L2 Cache", H.Bytes(l2 * 1024.0));
                if (l3 > 0) g.Add("L3 Cache", H.Bytes(l3 * 1024.0));
                g.Add("Address Width", S(o, "AddressWidth") + "-bit");
                g.Add("Current Load", S(o, "LoadPercentage") != "" ? S(o, "LoadPercentage") + " %" : "");
                g.Add("Virtualization (VT-x / AMD-V)", S(o, "VirtualizationFirmwareEnabled") == "True" ? "Enabled in firmware" : (S(o, "VirtualizationFirmwareEnabled") == "False" ? "Disabled in firmware (or Hyper-V active)" : ""));
                g.Add("SLAT", S(o, "SecondLevelAddressTranslationExtensions"));
                g.Add("Status", S(o, "Status"));
                if (n == 1) { bd.CpuName = S(o, "Name"); bd.Socket = S(o, "SocketDesignation"); bd.CpuVendor = S(o, "Manufacturer").Contains("AMD") ? "AMD" : S(o, "Manufacturer").Contains("Intel") ? "Intel" : ""; }
                if (n == 1) sumCpu = S(o, "Name") + "  ·  " + S(o, "NumberOfCores") + "C/" + S(o, "NumberOfLogicalProcessors") + "T  ·  " + S(o, "SocketDesignation");
            }
            if (n > 1) sumCpu += "  (x" + n + ")";
            var caches = Q("SELECT * FROM Win32_CacheMemory");
            if (caches.Count > 0)
            {
                var g = p.G("Cache (from SMBIOS)");
                foreach (var o in caches)
                    g.Add(S(o, "Purpose"), H.Bytes(U(o, "InstalledSize") * 1024.0));
            }
            var f = p.G("Instruction sets");
            var feats = new[] {
                new KeyValuePair<string,uint>("SSE",6), new KeyValuePair<string,uint>("SSE2",10), new KeyValuePair<string,uint>("SSE3",13),
                new KeyValuePair<string,uint>("SSSE3",36), new KeyValuePair<string,uint>("SSE4.1",37), new KeyValuePair<string,uint>("SSE4.2",38),
                new KeyValuePair<string,uint>("AVX",39), new KeyValuePair<string,uint>("AVX2",40), new KeyValuePair<string,uint>("AVX-512F",41),
                new KeyValuePair<string,uint>("NX / DEP",12), new KeyValuePair<string,uint>("RDRAND",28), new KeyValuePair<string,uint>("Virtualization enabled",21),
                new KeyValuePair<string,uint>("Second Level Address Translation",20)
            };
            var yes = new List<string>();
            foreach (var kv in feats)
            {
                bool ok = false;
                try { ok = H.IsProcessorFeaturePresent(kv.Value); } catch { }
                if (ok) yes.Add(kv.Key);
            }
            f.Add("Supported", string.Join(", ", yes));
            return p;
        }

        static readonly Dictionary<int, string> MemTypes = new Dictionary<int, string>
        { {20,"DDR"},{21,"DDR2"},{22,"DDR2 FB-DIMM"},{24,"DDR3"},{26,"DDR4"},{27,"LPDDR"},{28,"LPDDR2"},{29,"LPDDR3"},{30,"LPDDR4"},{34,"DDR5"},{35,"LPDDR5"} };
        static readonly Dictionary<int, string> FormFactors = new Dictionary<int, string>
        { {8,"DIMM"},{12,"SO-DIMM"},{13,"SRIMM"},{0,"Unknown"},{1,"Other"},{9,"TSOP"},{11,"FB-DIMM"},{14,"Row of chips"} };
        static readonly Dictionary<string, string> Jedec = new Dictionary<string, string>
        { {"80CE","Samsung"},{"00CE","Samsung"},{"802C","Micron"},{"002C","Micron"},{"80AD","SK hynix"},{"00AD","SK hynix"},
          {"859B","Crucial"},{"059B","Crucial"},{"0198","Kingston"},{"8198","Kingston"},{"029E","Corsair"},{"829E","Corsair"},
          {"04CD","G.Skill"},{"84CD","G.Skill"},{"04CB","ADATA"},{"84CB","ADATA"} };

        static string MemVendor(string m)
        {
            if (m == "") return "";
            string up = m.ToUpperInvariant();
            foreach (var kv in Jedec)
                if (up.StartsWith(kv.Key)) return kv.Value + "  (JEDEC " + m + ")";
            return m;
        }

        InfoPage MemoryPage()
        {
            var p = new InfoPage { Title = "Memory" };
            var mods = Q("SELECT * FROM Win32_PhysicalMemory");
            var arrays = Q("SELECT * FROM Win32_PhysicalMemoryArray");
            ulong total = 0;
            foreach (var m in mods) total += U(m, "Capacity");
            int slotsTotal = 0; ulong maxKb = 0;
            foreach (var a in arrays)
            {
                if (L(a, "Use") != 3 && L(a, "Use") != -1) continue;
                slotsTotal += (int)Math.Max(0, L(a, "MemoryDevices"));
                ulong mx = U(a, "MaxCapacityEx");
                if (mx == 0) mx = U(a, "MaxCapacity");
                maxKb += mx;
            }
            string type = "";
            long speed = 0;
            if (mods.Count > 0)
            {
                type = H.Pick(MemTypes, L(mods[0], "SMBIOSMemoryType"));
                speed = L(mods[0], "ConfiguredClockSpeed");
                if (speed <= 0) speed = L(mods[0], "Speed");
            }
            var g = p.G("General");
            g.Add("Total Installed", H.Bytes(total));
            foreach (var o in Q("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
            {
                double vis = U(o, "TotalVisibleMemorySize") * 1024.0, free = U(o, "FreePhysicalMemory") * 1024.0;
                g.Add("Usable by Windows", H.Bytes(vis));
                if (vis > 0) g.Add("In Use", H.Bytes(vis - free) + "  (" + ((vis - free) / vis * 100).ToString("0") + " %)");
            }
            g.Add("Type", type.StartsWith("Code") ? "" : type);
            g.Add("Speed", speed > 0 ? speed + " MT/s" : "");
            g.Add("Slots", slotsTotal > 0 ? mods.Count + " used of " + slotsTotal : mods.Count + " used");
            g.Add("Max Supported", maxKb > 0 ? H.Bytes(maxKb * 1024.0) : "");
            bd.MemSlots = slotsTotal;
            sumRam = H.Bytes(total) + " " + (type.StartsWith("Code") ? "" : type) + (speed > 0 ? " @ " + speed + " MT/s" : "") + "  ·  " + mods.Count + (slotsTotal > 0 ? "/" + slotsTotal : "") + " slots";

            foreach (var m in mods)
            {
                string loc = S(m, "DeviceLocator"), bank = S(m, "BankLabel");
                var mg = p.G("Module: " + loc + (bank != "" && bank != loc ? "  (" + bank + ")" : ""));
                mg.Add("Size", H.Bytes(U(m, "Capacity")));
                bd.Modules.Add(new MemRef { Locator = loc, Size = H.Bytes(U(m, "Capacity")), Group = mg });
                mg.Add("Type", H.Pick(MemTypes, L(m, "SMBIOSMemoryType")));
                mg.Add("Form Factor", H.Pick(FormFactors, L(m, "FormFactor")));
                mg.Add("Rated Speed", L(m, "Speed") > 0 ? S(m, "Speed") + " MT/s" : "");
                mg.Add("Configured Speed", L(m, "ConfiguredClockSpeed") > 0 ? S(m, "ConfiguredClockSpeed") + " MT/s" : "");
                mg.Add("Voltage", L(m, "ConfiguredVoltage") > 0 ? (L(m, "ConfiguredVoltage") / 1000.0).ToString("0.00") + " V" : "");
                mg.Add("Manufacturer", MemVendor(S(m, "Manufacturer")));
                mg.Add("Part Number", S(m, "PartNumber"));
                mg.Add("Serial Number", H.Clean(S(m, "SerialNumber")));
                mg.Add("Data Width", L(m, "DataWidth") > 0 ? S(m, "DataWidth") + "-bit" + (L(m, "TotalWidth") > L(m, "DataWidth") ? " (ECC: total " + S(m, "TotalWidth") + "-bit)" : "") : "");
            }
            return p;
        }

        Dictionary<string, ulong> ReadVram()
        {
            var vram = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return vram;
                    foreach (var sk in cls.GetSubKeyNames())
                    {
                        if (!Regex.IsMatch(sk, @"^\d{4}$")) continue;
                        try
                        {
                            using (var k = cls.OpenSubKey(sk))
                            {
                                if (k == null) continue;
                                var desc = k.GetValue("DriverDesc") as string;
                                ulong m = H.ObjToULong(k.GetValue("HardwareInformation.qwMemorySize"));
                                if (m == 0) m = H.ObjToULong(k.GetValue("HardwareInformation.MemorySize"));
                                if (desc != null && m > 0 && !vram.ContainsKey(desc)) vram[desc] = m;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return vram;
        }

        InfoPage GpuPage()
        {
            var p = new InfoPage { Title = "Graphics" };
            var vram = ReadVram();
            foreach (var o in Q("SELECT * FROM Win32_VideoController"))
            {
                string name = S(o, "Name");
                var g = p.G(name);
                g.Add("Name", name);
                g.Add("Vendor", S(o, "AdapterCompatibility"));
                g.Add("GPU", S(o, "VideoProcessor"));
                ulong mem;
                if (!vram.TryGetValue(name, out mem)) mem = U(o, "AdapterRAM");
                g.Add("Dedicated Memory", mem > 0 ? H.Bytes(mem) : "");
                g.Add("Driver Version", S(o, "DriverVersion"));
                g.Add("Driver Date", H.Dmtf(S(o, "DriverDate"), "yyyy-MM-dd"));
                long w = L(o, "CurrentHorizontalResolution"), h = L(o, "CurrentVerticalResolution");
                if (w > 0) g.Add("Current Mode", w + " x " + h + "  @ " + S(o, "CurrentRefreshRate") + " Hz, " + S(o, "CurrentBitsPerPixel") + "-bit");
                g.Add("PCI ID", H.Ids(S(o, "PNPDeviceID")));
                g.Add("Device ID", S(o, "PNPDeviceID"));
                g.Add("Status", S(o, "Status"));
                sumGpu.Add(name + (mem > 0 ? "  ·  " + H.Bytes(mem) : ""));
            }
            return p;
        }

        static readonly Dictionary<int, string> VideoOut = new Dictionary<int, string>
        { {-2,"Uninitialized"},{-1,"Other"},{0,"VGA (HD15)"},{1,"S-Video"},{2,"Composite"},{3,"Component"},{4,"DVI"},{5,"HDMI"},{6,"LVDS (internal)"},{8,"D-Jpn"},{9,"SDI"},{10,"DisplayPort"},{11,"Embedded DisplayPort (internal)"},{12,"UDI"},{13,"UDI (embedded)"},{14,"SDTV dongle"},{15,"Miracast (wireless)"},{16,"Indirect wired"} };
        static readonly Dictionary<string, string> PnpMfr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { {"SAM","Samsung"},{"SEC","Samsung"},{"SDC","Samsung Display"},{"DEL","Dell"},{"GSM","LG"},{"LGD","LG Display"},{"AUS","ASUS"},{"ACR","Acer"},{"BNQ","BenQ"},{"AOC","AOC"},{"HWP","HP"},{"HPN","HP"},{"LEN","Lenovo"},{"PHL","Philips"},{"VSC","ViewSonic"},{"MSI","MSI"},{"BOE","BOE"},{"AUO","AU Optronics"},{"CMN","Innolux"},{"SHP","Sharp"},{"GBT","Gigabyte"},{"HKC","HKC"},{"IVM","iiyama"},{"NEC","NEC"},{"SNY","Sony"},{"APP","Apple"},{"MEI","Panasonic"},{"XMI","Xiaomi"},{"KTC","KTC"},{"HSD","HannStar"},{"CSO","CSOT"},{"IVO","InfoVision"} };

        InfoPage MonitorPage()
        {
            var p = new InfoPage { Title = "Monitors" };
            var ids = Q(@"root\wmi", "SELECT * FROM WmiMonitorID");
            var basic = Q(@"root\wmi", "SELECT * FROM WmiMonitorBasicDisplayParams");
            var conn = Q(@"root\wmi", "SELECT * FROM WmiMonitorConnectionParams");
            int n = 0;
            foreach (var o in ids)
            {
                n++;
                string inst = S(o, "InstanceName");
                string name = H.WmiChars(o, "UserFriendlyName");
                string mfr = H.WmiChars(o, "ManufacturerName");
                string mfrName;
                if (PnpMfr.TryGetValue(mfr, out mfrName)) mfrName = mfrName + " (" + mfr + ")"; else mfrName = mfr;
                string title = name != "" ? name : "Monitor " + n;
                var g = p.G("Monitor #" + n + ": " + title);
                g.Add("Model", name);
                g.Add("Manufacturer", mfrName);
                g.Add("Product Code", H.WmiChars(o, "ProductCodeID"));
                g.Add("Serial Number", H.WmiChars(o, "SerialNumberID"));
                string year = S(o, "YearOfManufacture"), week = S(o, "WeekOfManufacture");
                if (year != "" && year != "0") g.Add("Manufactured", year + (week != "" && week != "0" && week != "255" ? ", week " + week : ""));
                string size = "";
                var b = basic.FirstOrDefault(x => S(x, "InstanceName") == inst);
                if (b != null)
                {
                    double w = U(b, "MaxHorizontalImageSize"), h = U(b, "MaxVerticalImageSize");
                    if (w > 0 && h > 0)
                    {
                        double diag = Math.Sqrt(w * w + h * h) / 2.54;
                        size = diag.ToString("0.0") + "\"";
                        g.Add("Screen Size", size + "  (" + w + " x " + h + " cm)");
                    }
                }
                string iface = "";
                var c = conn.FirstOrDefault(x => S(x, "InstanceName") == inst);
                if (c != null)
                {
                    long vot = L(c, "VideoOutputTechnology");
                    if (vot == 0x80000000L || vot == int.MinValue) iface = "Internal panel";
                    else iface = H.Pick(VideoOut, vot);
                    g.Add("Connection", iface);
                }
                g.Add("Instance", inst);
                sumMon.Add((mfrName != "" ? mfrName + " " : "") + name + (size != "" ? "  ·  " + size : "") + (iface != "" ? "  ·  " + iface : ""));
            }
            if (n == 0)
            {
                var g = p.G("Monitors (from Plug and Play)");
                AddDeviceList(g, snap.Devices.Where(d => d.Class.Equals("Monitor", StringComparison.OrdinalIgnoreCase)));
            }
            return p;
        }

        static readonly Dictionary<int, string> MediaTypes = new Dictionary<int, string> { {0,"Unspecified"},{3,"HDD"},{4,"SSD"},{5,"SCM"} };
        static readonly Dictionary<int, string> BusTypes = new Dictionary<int, string>
        { {0,"Unknown"},{1,"SCSI"},{2,"ATAPI"},{3,"ATA"},{4,"IEEE 1394"},{5,"SSA"},{6,"Fibre Channel"},{7,"USB"},{8,"RAID"},{9,"iSCSI"},{10,"SAS"},{11,"SATA"},{12,"SD"},{13,"MMC"},{14,"Virtual"},{15,"File-backed virtual"},{16,"Storage Spaces"},{17,"NVMe"},{18,"SCM"},{19,"UFS"} };
        static readonly Dictionary<int, string> Health = new Dictionary<int, string> { {0,"Healthy"},{1,"Warning"},{2,"Unhealthy"},{5,"Unknown"} };
        static readonly Dictionary<int, string> DriveTypes = new Dictionary<int, string> { {2,"Removable"},{3,"Local disk"},{4,"Network"},{5,"Optical"},{6,"RAM disk"} };

        InfoPage StoragePage()
        {
            var p = new InfoPage { Title = "Storage" };
            var pd = Q(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk");
            foreach (var d in Q("SELECT * FROM Win32_DiskDrive").OrderBy(x => L(x, "Index")))
            {
                string idx = S(d, "Index"), model = S(d, "Model");
                var m = pd.FirstOrDefault(x => S(x, "DeviceId") == idx);
                var g = p.G("Disk " + idx + ": " + model);
                ulong size = U(d, "Size");
                g.Add("Model", model);
                g.Add("Capacity", H.Bytes(size) + "  (" + H.DecimalSize(size) + " decimal)");
                string media = "", bus = S(d, "InterfaceType");
                if (m != null)
                {
                    media = H.Pick(MediaTypes, L(m, "MediaType"));
                    bus = H.Pick(BusTypes, L(m, "BusType"));
                    g.Add("Media Type", media);
                    g.Add("Bus", bus);
                    g.Add("Health", H.Pick(Health, L(m, "HealthStatus")));
                    long rpm = L(m, "SpindleSpeed");
                    if (rpm > 0 && rpm < 100000) g.Add("Spindle Speed", rpm + " RPM");
                }
                else g.Add("Interface", bus);
                g.Add("Firmware", S(d, "FirmwareRevision"));
                g.Add("Serial Number", S(d, "SerialNumber"));
                g.Add("Partitions", S(d, "Partitions"));
                g.Add("Status", S(d, "Status"));
                g.Add("Device ID", S(d, "PNPDeviceID"));
                string dpnp = S(d, "PNPDeviceID");
                var ddev = snap.Devices.FirstOrDefault(x => x.DeviceId.Equals(dpnp, StringComparison.OrdinalIgnoreCase));
                bd.Disks.Add(new DiskRef { Model = model, Size = H.DecimalSize(size), Media = media, BusType = bus, PnpId = dpnp,
                    ParentId = ddev != null ? ddev.ParentId : Native.Parent(dpnp), ScsiBus = (int)L(d, "SCSIBus"), Group = g });
                sumDisk.Add(model + "  ·  " + H.DecimalSize(size) + (media != "" && media != "Unspecified" ? "  ·  " + media : "") + (bus != "" ? "  ·  " + bus : ""));
            }
            var vols = Q("SELECT * FROM Win32_LogicalDisk");
            if (vols.Count > 0)
            {
                var g = p.G("Volumes");
                foreach (var v in vols)
                {
                    ulong size = U(v, "Size"), free = U(v, "FreeSpace");
                    string label = S(v, "VolumeName");
                    string val = H.Pick(DriveTypes, L(v, "DriveType"));
                    if (S(v, "FileSystem") != "") val += "  ·  " + S(v, "FileSystem");
                    if (size > 0) val += "  ·  " + H.Bytes(free) + " free of " + H.Bytes(size) + "  (" + (100.0 * (size - free) / size).ToString("0") + "% used)";
                    g.Add(S(v, "DeviceID") + (label != "" ? " " + label : ""), val);
                }
            }
            var cd = Q("SELECT * FROM Win32_CDROMDrive");
            if (cd.Count > 0)
            {
                var g = p.G("Optical drives");
                foreach (var o in cd) g.Add(S(o, "Drive"), S(o, "Name"));
            }
            var ctrls = snap.Devices.Where(d => !d.Virtual && (d.Class.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase) || d.Class.Equals("HDC", StringComparison.OrdinalIgnoreCase)));
            var cg = p.G("Storage controllers");
            AddDeviceList(cg, ctrls);
            return p;
        }

        static readonly Dictionary<int, string> NetStatus = new Dictionary<int, string>
        { {0,"Disconnected"},{1,"Connecting"},{2,"Connected"},{3,"Disconnecting"},{4,"Hardware not present"},{5,"Hardware disabled"},{6,"Hardware malfunction"},{7,"Media disconnected"},{8,"Authenticating"},{9,"Authentication succeeded"},{10,"Authentication failed"},{11,"Invalid address"},{12,"Credentials required"} };

        static string Speed(ulong bps)
        {
            if (bps == 0 || bps >= 9223372036854775807UL) return "";
            if (bps >= 1000000000) return (bps / 1e9).ToString("0.#") + " Gbps";
            return (bps / 1e6).ToString("0") + " Mbps";
        }

        InfoPage NetworkPage()
        {
            var p = new InfoPage { Title = "Network" };
            var cfgs = Q("SELECT Index, IPAddress, DefaultIPGateway, DHCPEnabled, DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
            foreach (var a in Q("SELECT * FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE"))
            {
                string pnp = S(a, "PNPDeviceID");
                string bus = pnp.Contains("\\") ? pnp.Substring(0, pnp.IndexOf('\\')) : "";
                bool hw = bus == "PCI" || bus == "USB" || bus.StartsWith("SD", StringComparison.OrdinalIgnoreCase) || bus == "BTH";
                string name = S(a, "Name");
                var g = p.G(name + (hw ? "" : "  (virtual)"));
                g.Add("Adapter", name);
                g.Add("Manufacturer", S(a, "Manufacturer"));
                g.Add("Connection Name", S(a, "NetConnectionID"));
                string st = H.Pick(NetStatus, L(a, "NetConnectionStatus"));
                g.Add("Status", st);
                g.Add("MAC Address", S(a, "MACAddress"));
                string sp = Speed(U(a, "Speed"));
                g.Add("Link Speed", sp);
                g.Add("Bus", bus);
                g.Add("Hardware ID", H.Ids(pnp));
                string idx = S(a, "Index");
                var c = cfgs.FirstOrDefault(x => S(x, "Index") == idx);
                if (c != null)
                {
                    g.Add("IP Address", S(c, "IPAddress"));
                    g.Add("Gateway", S(c, "DefaultIPGateway"));
                    g.Add("DNS Servers", S(c, "DNSServerSearchOrder"));
                    g.Add("DHCP", S(c, "DHCPEnabled") == "True" ? "Enabled" : "Disabled");
                }
                if (hw) sumNet.Add(name + "  ·  " + st + (sp != "" ? " @ " + sp : ""));
            }
            var bt = Real("Bluetooth").ToList();
            if (bt.Count > 0) AddDeviceList(p.G("Bluetooth"), bt);
            return p;
        }

        InfoPage AudioPage()
        {
            var p = new InfoPage { Title = "Audio" };
            var g = p.G("Sound devices");
            foreach (var o in Q("SELECT * FROM Win32_SoundDevice"))
                g.Add(S(o, "Name"), S(o, "Manufacturer") + "  ·  " + S(o, "Status") + (H.Ids(S(o, "PNPDeviceID")) != "" ? "  ·  " + H.Ids(S(o, "PNPDeviceID")) : ""));
            AddDeviceList(p.G("Audio inputs and outputs (endpoints)"), snap.Devices.Where(d => d.Class.Equals("AudioEndpoint", StringComparison.OrdinalIgnoreCase)));
            return p;
        }

        InfoPage UsbPage()
        {
            var p = new InfoPage { Title = "USB" };
            var cg = p.G("USB host controllers");
            foreach (var o in Q("SELECT * FROM Win32_USBController"))
                cg.Add(S(o, "Name"), H.Ids(S(o, "PNPDeviceID")) != "" ? H.Ids(S(o, "PNPDeviceID")) : S(o, "Manufacturer"));
            var usb = snap.Devices.Where(d => d.DeviceId.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase) && d.DeviceId.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            var hubs = usb.Where(d => d.Name.IndexOf("hub", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var devs = usb.Where(d => !hubs.Contains(d)).ToList();
            var dg = p.G("Connected USB devices - physical (" + devs.Count + ")");
            foreach (var d in devs.OrderBy(x => x.DeviceId))
            {
                string val = H.Ids(d.DeviceId) + "  ·  " + H.ClassName(d.Class);
                if (d.ErrorCode != 0) val = "[!] " + H.ErrorText(d.ErrorCode) + "  ·  " + val;
                dg.Add(d.Name, val);
            }
            AddDeviceList(p.G("USB hubs"), hubs);
            var roots = snap.Devices.Where(d => d.DeviceId.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase));
            AddDeviceList(p.G("Root hubs"), roots);
            return p;
        }

        InfoPage PeripheralPage()
        {
            var p = new InfoPage { Title = "Peripherals" };
            string[] classes = { "Keyboard", "Mouse", "Camera", "Image", "Biometric", "SmartCardReader", "Sensor", "WPD", "Printer", "Ports", "Battery", "SDHost", "SecurityDevices", "Firmware", "HIDClass" };
            foreach (var c in classes)
            {
                var list = Real(c).ToList();
                if (list.Count == 0) continue;
                AddDeviceList(p.G(H.ClassName(c) + " (" + list.Count + ")"), list);
            }
            foreach (var o in Q("SELECT * FROM Win32_Battery"))
            {
                var g = p.G("Battery: " + S(o, "Name"));
                g.Add("Charge", S(o, "EstimatedChargeRemaining") + " %");
                g.Add("Chemistry", S(o, "Chemistry"));
                g.Add("Status", S(o, "Status"));
            }
            return p;
        }
    }

    // ------------------------------------------------------------------- UI
    class MainForm : Form
    {
        readonly TabControl tabs;
        readonly ToolStripButton btnRefresh, btnExport, btnCopy;
        readonly ToolStripStatusLabel status;
        readonly BackgroundWorker worker;
        Snapshot snap;
        readonly float scale;
        readonly bool isAdmin;

        int Px(int v) { return (int)Math.Round(v * scale); }

        public MainForm()
        {
            using (var gr = CreateGraphics()) scale = gr.DpiX / 96f;
            try { isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { }

            Text = "HW Inspector - " + Environment.MachineName;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(Px(1120), Px(740));
            MinimumSize = new Size(Px(700), Px(450));
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var ts = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(Px(6), Px(3), Px(6), Px(3)), RenderMode = ToolStripRenderMode.System };
            btnRefresh = new ToolStripButton("Refresh (F5)");
            btnExport = new ToolStripButton("Export report...");
            btnCopy = new ToolStripButton("Copy this tab");
            btnRefresh.Click += delegate { StartScan(); };
            btnExport.Click += delegate { Export(); };
            btnCopy.Click += delegate { CopyTab(); };
            ts.Items.Add(btnRefresh);
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(btnExport);
            ts.Items.Add(btnCopy);
            var adminLbl = new ToolStripLabel(isAdmin ? "Running as Administrator" : "Tip: run as Administrator for extra details") { Alignment = ToolStripItemAlignment.Right, ForeColor = SystemColors.GrayText };
            ts.Items.Add(adminLbl);

            tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(Px(12), Px(4)) };
            var ss = new StatusStrip();
            status = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            ss.Items.Add(status);

            Controls.Add(tabs);
            Controls.Add(ts);
            Controls.Add(ss);

            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.F5) StartScan(); };

            worker = new BackgroundWorker { WorkerReportsProgress = true };
            worker.DoWork += (s, e) =>
            {
                e.Result = new Collector().Run(msg => worker.ReportProgress(0, msg));
            };
            worker.ProgressChanged += (s, e) => { status.Text = "Scanning: " + e.UserState; };
            worker.RunWorkerCompleted += (s, e) => ScanDone(e);
            Shown += delegate { StartScan(); };
        }

        void SetBusy(bool busy)
        {
            btnRefresh.Enabled = !busy;
            btnExport.Enabled = !busy && snap != null;
            btnCopy.Enabled = !busy && snap != null;
            UseWaitCursor = busy;
        }

        void StartScan()
        {
            if (worker.IsBusy) return;
            tabs.TabPages.Clear();
            var tp = new TabPage("Scanning...");
            tp.Controls.Add(new Label { Text = "Scanning hardware, please wait...", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 12f) });
            tabs.TabPages.Add(tp);
            SetBusy(true);
            worker.RunWorkerAsync();
        }

        void ScanDone(RunWorkerCompletedEventArgs e)
        {
            tabs.TabPages.Clear();
            if (e.Error != null)
            {
                snap = null;
                SetBusy(false);
                status.Text = "Scan failed: " + e.Error.Message;
                MessageBox.Show(this, e.Error.ToString(), "Scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            snap = (Snapshot)e.Result;
            tabs.SuspendLayout();
            if (snap.Board != null)
            {
                var bt = new TabPage("Board 3D") { Tag = "board" };
                try { bt.Controls.Add(MakeBoardView(snap.Board)); }
                catch (Exception ex) { bt.Controls.Add(new Label { Text = "3D view failed: " + ex.Message, Dock = DockStyle.Fill }); }
                tabs.TabPages.Add(bt);
            }
            foreach (var page in snap.Pages)
            {
                var tp = new TabPage(page.Title) { Tag = page };
                tp.Controls.Add(MakeList(page));
                tabs.TabPages.Add(tp);
            }
            var dt = new TabPage("All Devices (" + snap.Devices.Count + ")") { Tag = "devices" };
            dt.Controls.Add(MakeDeviceView(snap.Devices));
            tabs.TabPages.Add(dt);
            tabs.ResumeLayout();
            int bad = snap.Devices.Count(d => d.ErrorCode != 0);
            status.Text = string.Format("Scanned {0:yyyy-MM-dd HH:mm:ss} in {1:0.0}s  ·  {2} devices detected  ·  {3}",
                snap.Time, snap.Duration.TotalSeconds, snap.Devices.Count, bad == 0 ? "no device problems" : bad + " device(s) with problems");
            SetBusy(false);
        }

        ListView NewListView()
        {
            var lv = new ListView
            {
                View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowItemToolTips = true, BorderStyle = BorderStyle.None
            };
            lv.Columns.Add("Property", Px(280));
            lv.Columns.Add("Value", Px(760));
            lv.Resize += (s, e) =>
            {
                int w = lv.ClientSize.Width - lv.Columns[0].Width - 4;
                if (w > Px(200)) lv.Columns[1].Width = w;
            };
            lv.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem it in lv.Items) it.Selected = true; }
                if (e.Control && e.KeyCode == Keys.C)
                {
                    var sb = new StringBuilder();
                    foreach (ListViewItem it in lv.SelectedItems) sb.AppendLine(it.Text + "\t" + it.SubItems[1].Text);
                    if (sb.Length > 0) Clipboard.SetText(sb.ToString());
                }
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Copy value", null, delegate { if (lv.SelectedItems.Count > 0) Clipboard.SetText(lv.SelectedItems[0].SubItems[1].Text); });
            menu.Items.Add("Copy row(s)", null, delegate
            {
                var sb = new StringBuilder();
                foreach (ListViewItem it in lv.SelectedItems) sb.AppendLine(it.Text + "\t" + it.SubItems[1].Text);
                if (sb.Length > 0) Clipboard.SetText(sb.ToString());
            });
            lv.ContextMenuStrip = menu;
            return lv;
        }

        ListView MakeList(InfoPage page)
        {
            var lv = NewListView();
            lv.BeginUpdate();
            foreach (var g in page.Groups)
            {
                if (g.Rows.Count == 0) continue;
                var lg = new ListViewGroup(g.Name);
                lv.Groups.Add(lg);
                foreach (var r in g.Rows)
                {
                    var it = new ListViewItem(new[] { r[0], r[1] }, lg);
                    if (r[1].StartsWith("[!]") || r[0].StartsWith("  [!]")) it.ForeColor = Color.Firebrick;
                    lv.Items.Add(it);
                }
            }
            if (lv.Items.Count == 0) lv.Items.Add(new ListViewItem(new[] { "(nothing detected)", "" }));
            lv.EndUpdate();
            return lv;
        }

        Control MakeBoardView(BoardLayout L)
        {
            var split = new SplitContainer { Size = new Size(Px(1100), Px(620)), FixedPanel = FixedPanel.Panel2 };
            split.Dock = DockStyle.Fill;
            bool splitSet = false;
            split.SizeChanged += delegate
            {
                if (splitSet || split.Width < Px(600)) return;
                splitSet = true;
                split.SplitterDistance = Math.Max(Px(300), split.Width - Px(440));
            };

            var view = new Board3DView();
            var host = new ElementHost { Dock = DockStyle.Fill, Child = view };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(Px(4)) };
            Func<string, Button> btn = t => new Button { Text = t, AutoSize = true, FlatStyle = FlatStyle.System, Margin = new Padding(Px(3)) };
            var bReset = btn("Reset view");
            var bTop = btn("Top view");
            var bFront = btn("Side view");
            var chkCards = new CheckBox { Text = "Show add-in cards", Checked = true, AutoSize = true, Margin = new Padding(Px(12), Px(8), Px(3), Px(3)) };
            var bSave = btn("Save layout file...");
            var info = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(Px(12), Px(9), Px(3), Px(3)),
                                   Text = L.FormFactor + "  ·  " + L.Width.ToString("0") + " x " + L.Height.ToString("0") + " mm" + (L.ProfileLoaded ? "  ·  custom layout" : "  ·  auto layout") };
            bar.Controls.AddRange(new Control[] { bReset, bTop, bFront, chkCards, bSave, info });
            split.Panel1.Controls.Add(host);
            split.Panel1.Controls.Add(bar);

            var title = new Label { Dock = DockStyle.Top, Height = Px(54), Font = new Font("Segoe UI Semibold", 11f), Padding = new Padding(Px(8), 0, Px(4), 0),
                                    TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            var list = NewListView();
            list.Columns[0].Width = Px(150);
            split.Panel2.Controls.Add(list);
            split.Panel2.Controls.Add(title);

            var pcb = L.Parts.FirstOrDefault(p => p.Kind == "pcb");
            Action<BoardComponent> show = c =>
            {
                if (c == null) c = pcb;
                list.BeginUpdate();
                list.Items.Clear();
                list.Groups.Clear();
                title.Text = c == null ? L.Title : c.Name;
                if (c != null)
                {
                    foreach (var g in c.Details)
                    {
                        if (g.Rows.Count == 0) continue;
                        var lg = new ListViewGroup(g.Name);
                        list.Groups.Add(lg);
                        foreach (var r in g.Rows) list.Items.Add(new ListViewItem(new[] { r[0], r[1] }, lg));
                    }
                }
                if (list.Items.Count == 0) list.Items.Add(new ListViewItem(new[] { "(no details)", "" }));
                list.EndUpdate();
            };
            view.Selected += c => show(c);
            bReset.Click += delegate { view.ResetView(); };
            bTop.Click += delegate { view.TopView(); };
            bFront.Click += delegate { view.FrontView(); };
            chkCards.CheckedChanged += delegate { view.ShowCards = chkCards.Checked; };
            bSave.Click += delegate
            {
                try
                {
                    if (!File.Exists(L.ProfilePath)) BoardBuilder.WriteTemplate(L);
                    System.Diagnostics.Process.Start("notepad.exe", "\"" + L.ProfilePath + "\"");
                    status.Text = "Layout file: " + L.ProfilePath + "  (edit, save, then press Refresh)";
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Layout file", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            view.Load(L);
            show(null);
            return split;
        }

        Control MakeDeviceView(List<DeviceInfo> devs)
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(Px(4)), WrapContents = false };
            var lbl = new Label { Text = "Search:", AutoSize = true, Margin = new Padding(Px(3), Px(7), Px(3), Px(3)) };
            var txt = new TextBox { Width = Px(280) };
            var chkVirt = new CheckBox { Text = "Hide software / virtual devices", AutoSize = true, Checked = true, Margin = new Padding(Px(14), Px(5), Px(3), Px(3)) };
            var chkErr = new CheckBox { Text = "Only devices with problems", AutoSize = true, Margin = new Padding(Px(10), Px(5), Px(3), Px(3)) };
            var count = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(Px(14), Px(7), Px(3), Px(3)) };
            top.Controls.AddRange(new Control[] { lbl, txt, chkVirt, chkErr, count });

            var split = new SplitContainer { Size = new Size(Px(1100), Px(600)), FixedPanel = FixedPanel.Panel1 };
            split.SplitterDistance = Px(430);
            split.Dock = DockStyle.Fill;
            var tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, BorderStyle = BorderStyle.None, ShowLines = true };
            var detail = NewListView();
            detail.Columns[0].Width = Px(170);
            split.Panel1.Controls.Add(tree);
            split.Panel2.Controls.Add(detail);

            panel.Controls.Add(split);
            panel.Controls.Add(top);

            Action rebuild = () =>
            {
                string f = txt.Text.Trim().ToLowerInvariant();
                var shown = devs.Where(d => (!chkVirt.Checked || !d.Virtual) && (!chkErr.Checked || d.ErrorCode != 0) &&
                    (f == "" || (d.Name + " " + d.Class + " " + H.ClassName(d.Class) + " " + d.Manufacturer + " " + d.DeviceId).ToLowerInvariant().Contains(f))).ToList();
                tree.BeginUpdate();
                tree.Nodes.Clear();
                foreach (var g in shown.GroupBy(d => H.ClassName(d.Class)).OrderBy(g => g.Key))
                {
                    var n = tree.Nodes.Add(g.Key + "  (" + g.Count() + ")");
                    foreach (var d in g.OrderBy(x => x.Name))
                    {
                        var c = n.Nodes.Add(d.Name);
                        c.Tag = d;
                        if (d.ErrorCode != 0) { c.ForeColor = Color.Firebrick; n.ForeColor = Color.Firebrick; c.Text = "[!] " + d.Name; }
                        else if (d.Virtual) c.ForeColor = SystemColors.GrayText;
                    }
                }
                if (f != "" || chkErr.Checked) tree.ExpandAll();
                tree.EndUpdate();
                count.Text = shown.Count + " of " + devs.Count + " devices";
            };

            tree.AfterSelect += (s, e) =>
            {
                var d = e.Node.Tag as DeviceInfo;
                detail.BeginUpdate();
                detail.Items.Clear();
                if (d != null)
                {
                    Action<string, string> add = (k, v) => { if (!string.IsNullOrEmpty(v)) detail.Items.Add(new ListViewItem(new[] { k, v })); };
                    add("Name", d.Name);
                    add("Class", H.ClassName(d.Class) + "  (" + d.Class + ")");
                    add("Manufacturer", d.Manufacturer);
                    var st = new ListViewItem(new[] { "Status", H.ErrorText(d.ErrorCode) + (d.Status != "" ? "  [" + d.Status + "]" : "") });
                    if (d.ErrorCode != 0) st.ForeColor = Color.Firebrick;
                    detail.Items.Add(st);
                    add("Bus / Enumerator", d.Bus + (d.Virtual ? "  (software / virtual)" : ""));
                    add("Vendor:Device", H.Ids(d.DeviceId));
                    add("Driver Service", d.Service);
                    add("Instance ID", d.DeviceId);
                    if (!string.IsNullOrEmpty(d.HardwareIds))
                        foreach (var hid in d.HardwareIds.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)) add("Hardware ID", hid);
                }
                detail.EndUpdate();
            };

            txt.TextChanged += delegate { rebuild(); };
            chkVirt.CheckedChanged += delegate { rebuild(); };
            chkErr.CheckedChanged += delegate { rebuild(); };
            rebuild();
            return panel;
        }

        // ---------- text output
        static string PageText(InfoPage p)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[" + p.Title + "]");
            foreach (var g in p.Groups)
            {
                if (g.Rows.Count == 0) continue;
                sb.AppendLine("  -- " + g.Name + " --");
                int w = Math.Min(40, g.Rows.Max(r => r[0].Length));
                foreach (var r in g.Rows) sb.AppendLine("    " + r[0].PadRight(w) + " : " + r[1]);
            }
            sb.AppendLine();
            return sb.ToString();
        }

        static string DevicesText(List<DeviceInfo> devs)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[All Devices]");
            foreach (var g in devs.GroupBy(d => H.ClassName(d.Class)).OrderBy(g => g.Key))
            {
                sb.AppendLine("  -- " + g.Key + " (" + g.Count() + ") --");
                foreach (var d in g.OrderBy(x => x.Name))
                    sb.AppendLine("    " + (d.ErrorCode != 0 ? "[!] " : "") + d.Name + (d.Virtual ? " (virtual)" : "") + "   [" + d.DeviceId + "]" + (d.ErrorCode != 0 ? "  " + H.ErrorText(d.ErrorCode) : ""));
            }
            return sb.ToString();
        }

        string FullReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("HW Inspector report - " + snap.ComputerName + " - " + snap.Time.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(new string('=', 78));
            sb.AppendLine();
            foreach (var p in snap.Pages) sb.Append(PageText(p));
            sb.Append(DevicesText(snap.Devices));
            return sb.ToString();
        }

        void Export()
        {
            if (snap == null) return;
            using (var dlg = new System.Windows.Forms.SaveFileDialog { Filter = "Text file (*.txt)|*.txt", FileName = "HW-Report-" + snap.ComputerName + "-" + snap.Time.ToString("yyyyMMdd-HHmm") + ".txt" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(dlg.FileName, FullReport(), new UTF8Encoding(true));
                status.Text = "Report saved: " + dlg.FileName;
            }
        }

        void CopyTab()
        {
            if (snap == null || tabs.SelectedTab == null) return;
            var page = tabs.SelectedTab.Tag as InfoPage;
            string text = page != null ? PageText(page) : DevicesText(snap.Devices);
            Clipboard.SetText(text);
            status.Text = "Copied \"" + tabs.SelectedTab.Text + "\" to clipboard";
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
