// Board model: turns detected hardware (SMBIOS slots/connectors + PCI topology) into a
// physical board layout in millimetres, for the 3D view.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HwInspector
{
    class BoardComponent
    {
        public string Key = "";      // stable id (slot / connector name) used by layout profiles
        public string Anchor = "";   // Key of the part this one sits on (moves with it)
        public string Name = "";     // title shown on hover / in the detail panel
        public string Kind = "";
        public string Label = "";    // text printed on the top face
        public double X, Y, W, D, Z, H; // mm. X: from rear-I/O edge, Y: from top edge, Z: height above PCB
        public uint Color = 0xFF2B2B2B;
        public uint TextColor = 0xFFFFFFFF;
        public bool Clickable = true, IsCard, Cylinder, Shiny, Estimated;
        public int Pos = -1;         // expansion slot position (0 = top)
        public List<InfoGroup> Details = new List<InfoGroup>();
        public InfoGroup G(string name)
        {
            var g = new InfoGroup { Name = name };
            Details.Add(g);
            return g;
        }
    }

    class BoardLayout
    {
        public string Title = "", FormFactor = "", ProfilePath = "";
        public double Width, Height;
        public bool ProfileLoaded;
        public List<BoardComponent> Parts = new List<BoardComponent>();
        public List<string> Notes = new List<string>();
    }

    class SmbiosSlot
    {
        public string Name = "";
        public int Type, Width, Usage, Bus = -1, Dev = -1, Fn = -1;
        public int PhysLanes { get { int t = TypeLanes(); return t > 0 ? t : ElecLanes; } }
        public int ElecLanes
        {
            get
            {
                switch (Width) { case 8: return 1; case 9: return 2; case 10: return 4; case 11: return 8; case 12: return 12; case 13: return 16; case 14: return 32; default: return 0; }
            }
        }
        int TypeLanes()
        {
            switch (Type) { case 0xA6: return 1; case 0xA7: return 2; case 0xA8: return 4; case 0xA9: return 8; case 0xAA: return 16; default: return 0; }
        }
    }

    class Connector
    {
        public string Internal = "", External = "";
        public int PortType;
        public string Name { get { return Internal != "" ? Internal : External; } }
        public bool Rear { get { return External != ""; } }
    }

    class DiskRef
    {
        public string Model = "", Size = "", Media = "", BusType = "", PnpId = "", ParentId = "";
        public int ScsiBus = -1;
        public InfoGroup Group;
    }

    class MemRef
    {
        public string Locator = "", Size = "";
        public InfoGroup Group;
    }

    class BoardData
    {
        public string Manufacturer = "", Product = "", CpuName = "", Socket = "", CpuVendor = "";
        public List<SmbiosSlot> Slots = new List<SmbiosSlot>();
        public List<Connector> Connectors = new List<Connector>();
        public List<DiskRef> Disks = new List<DiskRef>();
        public List<MemRef> Modules = new List<MemRef>();
        public int MemSlots;
    }

    static class Native
    {
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int len, uint flags);

        public static string Parent(string id)
        {
            try
            {
                uint d, p;
                if (string.IsNullOrEmpty(id) || CM_Locate_DevNodeW(out d, id, 0) != 0) return "";
                if (CM_Get_Parent(out p, d, 0) != 0) return "";
                var sb = new StringBuilder(512);
                if (CM_Get_Device_IDW(p, sb, sb.Capacity, 0) != 0) return "";
                return sb.ToString();
            }
            catch { return ""; }
        }

        public static void PciLocation(DeviceInfo d)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + d.DeviceId))
                {
                    if (k == null) return;
                    string s = Convert.ToString(k.GetValue("LocationInformation"));
                    var m = Regex.Match(s, @"\((\d+),(\d+),(\d+)\)\s*$");
                    if (!m.Success) m = Regex.Match(s, @"bus (\d+), device (\d+), function (\d+)", RegexOptions.IgnoreCase);
                    if (!m.Success) return;
                    d.PciBus = int.Parse(m.Groups[1].Value);
                    d.PciDev = int.Parse(m.Groups[2].Value);
                    d.PciFn = int.Parse(m.Groups[3].Value);
                }
            }
            catch { }
        }

        // SMBIOS type 9 (System Slots) straight from the raw table: WMI's Win32_SystemSlot
        // does not expose the slot's PCI bus/device/function, which we need to find the card in it.
        public static List<SmbiosSlot> ReadSlots()
        {
            var list = new List<SmbiosSlot>();
            byte[] raw = null;
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT SMBiosData FROM MSSmBios_RawSMBiosTables"))
                foreach (ManagementObject o in s.Get()) { raw = o["SMBiosData"] as byte[]; break; }
            if (raw == null) return list;
            int i = 0;
            while (i + 4 <= raw.Length)
            {
                int type = raw[i], len = raw[i + 1];
                if (len < 4 || i + len > raw.Length) break;
                int j = i + len;
                var strs = new List<string>();
                var cur = new StringBuilder();
                while (j + 1 < raw.Length && !(raw[j] == 0 && raw[j + 1] == 0))
                {
                    if (raw[j] == 0) { strs.Add(cur.ToString()); cur.Clear(); }
                    else cur.Append((char)raw[j]);
                    j++;
                }
                if (cur.Length > 0) strs.Add(cur.ToString());
                if (type == 9 && len >= 0x0C)
                {
                    var sl = new SmbiosSlot();
                    int si = raw[i + 4];
                    sl.Name = si > 0 && si <= strs.Count ? strs[si - 1].Trim() : "Slot " + (list.Count + 1);
                    sl.Type = raw[i + 5];
                    sl.Width = raw[i + 6];
                    sl.Usage = raw[i + 7];
                    if (len >= 0x11)
                    {
                        int bus = raw[i + 0x0F], df = raw[i + 0x10];
                        if (df != 0xFF && bus != 0xFF) { sl.Bus = bus; sl.Dev = df >> 3; sl.Fn = df & 7; }
                    }
                    list.Add(sl);
                }
                if (type == 127) break;
                i = j + 2;
            }
            return list;
        }
    }

    static class BoardBuilder
    {
        const double Pitch = 20.32; // standard expansion slot spacing (0.8")

        static BoardComponent Part(BoardLayout L, string kind, string key, string name, double x, double y, double w, double d, double z, double h, uint color, string label)
        {
            var c = new BoardComponent { Kind = kind, Key = (key ?? "").Replace(' ', '_'), Name = name, X = x, Y = y, W = w, D = d, Z = z, H = h, Color = color, Label = label ?? "" };
            L.Parts.Add(c);
            return c;
        }

        static InfoGroup FindGroup(List<InfoPage> pages, string page, Func<InfoGroup, bool> pred)
        {
            var p = pages.FirstOrDefault(x => x.Title == page);
            return p == null ? null : p.Groups.FirstOrDefault(pred);
        }

        static IEnumerable<InfoGroup> PageGroups(List<InfoPage> pages, string page)
        {
            var p = pages.FirstOrDefault(x => x.Title == page);
            return p == null ? Enumerable.Empty<InfoGroup>() : p.Groups.Where(g => g.Rows.Count > 0);
        }

        static InfoGroup DeviceGroup(DeviceInfo d, string title)
        {
            var g = new InfoGroup { Name = title };
            g.Add("Name", d.Name);
            g.Add("Type", H.ClassName(d.Class));
            g.Add("Manufacturer", d.Manufacturer);
            g.Add("Vendor:Device", H.Ids(d.DeviceId));
            g.Add("Status", H.ErrorText(d.ErrorCode));
            if (d.PciBus >= 0) g.Add("PCI address", "bus " + d.PciBus + ", device " + d.PciDev + ", function " + d.PciFn);
            g.Add("Driver", d.Service);
            g.Add("Instance ID", d.DeviceId);
            return g;
        }

        static bool IsBridge(DeviceInfo d)
        {
            return Regex.IsMatch(d.Name, @"Bridge|Switch Port|Root Port|PCI Express Port|PCI-to-PCI|Downstream|Upstream", RegexOptions.IgnoreCase);
        }

        static bool Preferred(DeviceInfo d)
        {
            return Regex.IsMatch(d.Class, "^(Display|Net|MEDIA|SCSIAdapter|HDC|USB)$", RegexOptions.IgnoreCase);
        }

        static bool IsPci(DeviceInfo d) { return d.Bus.Equals("PCI", StringComparison.OrdinalIgnoreCase); }

        // From the slot's PCI function walk down (through switches/bridges) to the card itself.
        static DeviceInfo Endpoint(DeviceInfo start, ILookup<string, DeviceInfo> kids)
        {
            if (!IsBridge(start)) return start;
            var q = new Queue<DeviceInfo>();
            q.Enqueue(start);
            DeviceInfo fallback = null;
            int guard = 0;
            while (q.Count > 0 && guard++ < 300)
            {
                var cur = q.Dequeue();
                foreach (var k in kids[cur.DeviceId])
                {
                    if (!IsPci(k)) continue;
                    if (IsBridge(k)) { q.Enqueue(k); continue; }
                    if (Preferred(k)) return k;
                    if (fallback == null) fallback = k;
                }
            }
            return fallback;
        }

        static int Depth(DeviceInfo d, Dictionary<string, DeviceInfo> byId)
        {
            int n = 0;
            string p = d.ParentId;
            DeviceInfo pd;
            while (!string.IsNullOrEmpty(p) && byId.TryGetValue(p, out pd) && IsPci(pd) && n < 32) { n++; p = pd.ParentId; }
            return n;
        }

        static int TrailingNumber(string s)
        {
            var m = Regex.Match(s, @"(\d+)\D*$");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        static string Shorten(string s, int max)
        {
            s = Regex.Replace(s ?? "", @"\(R\)|\(TM\)|®|™|\d+-Core Processor|Processor|CPU @.*$|Corporation|Graphics Card", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > max ? s.Substring(0, max - 1) + "…" : s;
        }

        static List<string> DimmNames(BoardData bd)
        {
            var installed = bd.Modules.Select(m => m.Locator).Where(n => n != "").ToList();
            int total = Math.Max(bd.MemSlots, installed.Count);
            var names = new List<string>();
            var m0 = installed.Select(n => Regex.Match(n, @"^(.*?)([A-H])(\d)$")).FirstOrDefault(m => m.Success);
            if (m0 != null && total > 0)
            {
                string prefix = m0.Groups[1].Value;
                int ch = total <= 4 ? Math.Min(2, total) : 4;
                int per = (int)Math.Ceiling(total / (double)ch);
                for (int c = 0; c < ch; c++)
                    for (int k = 1; k <= per; k++) names.Add(prefix + (char)('A' + c) + k);
                foreach (var n in installed) if (!names.Contains(n)) names.Add(n);
                while (names.Count > total)
                {
                    var rm = names.LastOrDefault(n => !installed.Contains(n));
                    if (rm == null) break;
                    names.Remove(rm);
                }
            }
            else
            {
                names.AddRange(installed);
                for (int i = names.Count; i < total; i++) names.Add("DIMM " + (i + 1));
            }
            return names;
        }

        static string ProfileFile(BoardData bd)
        {
            string n = Regex.Replace((bd.Manufacturer + " " + bd.Product).Trim(), @"[^A-Za-z0-9_\-\. ]", "_");
            if (n == "") n = "unknown-board";
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layouts", n + ".txt");
        }

        public static BoardLayout Build(BoardData bd, Snapshot snap, List<InfoPage> pages)
        {
            var L = new BoardLayout();
            var devs = snap.Devices;
            var byId = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in devs) if (d.DeviceId != "" && !byId.ContainsKey(d.DeviceId)) byId[d.DeviceId] = d;
            var kids = devs.Where(d => d.ParentId != "").ToLookup(d => d.ParentId, StringComparer.OrdinalIgnoreCase);

            // ---- profile (optional per-board overrides)
            L.ProfilePath = ProfileFile(bd);
            var slotOverride = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var moves = new List<Tuple<string, double, double>>();
            if (File.Exists(L.ProfilePath))
            {
                L.ProfileLoaded = true;
                foreach (var raw in File.ReadAllLines(L.ProfilePath))
                {
                    var line = raw.Trim();
                    if (line == "" || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    int iv; double x, y;
                    if (t.Length >= 3 && t[0].Equals("slot", StringComparison.OrdinalIgnoreCase) && int.TryParse(t[2], out iv))
                        slotOverride[t[1]] = iv - 1;
                    else if (t.Length >= 4 && t[0].Equals("move", StringComparison.OrdinalIgnoreCase) &&
                             double.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                             double.TryParse(t[3], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                        moves.Add(Tuple.Create(t[1], x, y));
                }
            }

            // ---- form factor
            int nSlots = bd.Slots.Count;
            double W = 244, Hh = 305;
            int positions = 7;
            if (nSlots <= 1 && bd.MemSlots <= 2) { L.FormFactor = "Mini-ITX"; W = 170; Hh = 170; positions = 1; }
            else if (nSlots <= 4) { L.FormFactor = "Micro-ATX"; Hh = 244; positions = 4; }
            else { L.FormFactor = "ATX"; if (nSlots > 7) { positions = nSlots; Hh = 305 + (nSlots - 7) * Pitch; } }
            bool small = W < 200;
            L.Width = W; L.Height = Hh;
            L.Title = (bd.Manufacturer + " " + bd.Product).Trim();
            if (L.Title == "") L.Title = "Mainboard";

            double cpuCx = small ? 66 : 100, cpuCy = small ? 64 : 72;
            double dimmX0 = small ? 110 : 152;
            double slotY0 = small ? 140 : 162;
            double m2Y0 = small ? 104 : 120;

            // ---- PCB
            var pcb = Part(L, "pcb", "PCB", L.Title, 0, 0, W, Hh, -1.6, 1.6, 0xFF1D2127, bd.Product);
            pcb.TextColor = 0x30FFFFFF;
            foreach (var g in PageGroups(pages, "Mainboard").Where(g => g.Name == "Motherboard" || g.Name == "Chipset" || g.Name.StartsWith("BIOS")))
                pcb.Details.Add(g);

            // ---- power delivery
            var vl = Part(L, "vrm", "VRM_L", "VRM heatsink (CPU power stage)", small ? 10 : 28, small ? 12 : 18, 16, small ? 70 : 94, 0, 30, 0xFF50565E, "");
            var vt = Part(L, "vrm", "VRM_T", "VRM heatsink (CPU power stage)", small ? 30 : 54, 4, small ? 70 : 86, 16, 0, 28, 0xFF50565E, "");
            foreach (var v in new[] { vl, vt })
            {
                v.Shiny = true;
                var g = v.G("Voltage regulator module");
                g.Add("Function", "Converts 12 V from the EATX12V connector into CPU core voltage");
                g.Add("Note", "Phase count, VRM temperature and current are not exposed by Windows APIs");
            }
            var eps = Part(L, "power", "EATX12V", "EATX12V - CPU 8-pin power", small ? 8 : 30, 3, 20, 9, 0, 10, 0xFF151515, "8-pin");
            eps.G("Power connector").Add("Purpose", "12 V supply for the CPU VRM (from the PSU's EPS/CPU cable)");
            var atx = Part(L, "power", "EATXPWR", "EATXPWR - 24-pin ATX power", W - 15, small ? 30 : 88, 11, 52, 0, 12, 0xFF151515, "24-pin");
            atx.G("Power connector").Add("Purpose", "Main board power from the PSU (3.3 V / 5 V / 12 V rails)");

            // ---- CPU
            bool lga = Regex.IsMatch(bd.Socket, "LGA|AM5|1700|1851|1200|115", RegexOptions.IgnoreCase);
            var sock = Part(L, "socket", "CPU_SOCKET", "CPU socket " + bd.Socket, cpuCx - 30, cpuCy - 30, 60, 60, 0, 5, lga ? 0xFF2A2C30 : 0xFFE6E0CF, "");
            var cpu = Part(L, "cpu", "CPU", bd.CpuName == "" ? "Processor" : bd.CpuName, cpuCx - 20, cpuCy - 20, 40, 40, 5, 4, 0xFFC9CCD1, Shorten(bd.CpuName, 26));
            cpu.Anchor = "CPU_SOCKET";
            cpu.TextColor = 0xFF23262B;
            cpu.Shiny = true;
            foreach (var g in PageGroups(pages, "CPU")) { cpu.Details.Add(g); sock.Details.Add(g); }

            // ---- memory
            var dimmNames = DimmNames(bd);
            for (int i = 0; i < dimmNames.Count; i++)
            {
                string n = dimmNames[i];
                double x = dimmX0 + i * 9;
                var mod = bd.Modules.FirstOrDefault(m => m.Locator.Equals(n, StringComparison.OrdinalIgnoreCase));
                var slot = Part(L, "dimm", n, "Memory slot " + n + (mod == null ? " (empty)" : ""), x, 14, 6, 133, 0, 8, i % 2 == 0 ? 0xFF3C4046 : 0xFF2A2C30, n);
                var sg = slot.G("Memory slot");
                sg.Add("Slot", n);
                sg.Add("Status", mod == null ? "Empty" : "Installed: " + mod.Size);
                var cm = Regex.Match(n, @"([A-H])\d$");
                if (cm.Success) sg.Add("Channel", "Channel " + cm.Groups[1].Value);
                sg.Add("Position", "#" + (i + 1) + " counting from the CPU (inferred from slot names)");
                if (mod != null)
                {
                    if (mod.Group != null) slot.Details.Add(mod.Group);
                    var stick = Part(L, "module", n + "_MODULE", n + ": " + mod.Size + " memory module", x - 0.5, 16, 7, 129, 8, 34, 0xFF1C1C1E, mod.Size);
                    stick.Anchor = slot.Key;
                    stick.Details = slot.Details;
                }
            }

            // ---- expansion slots
            var usedDevs = new HashSet<DeviceInfo>();
            var free = new SortedSet<int>(Enumerable.Range(0, positions));
            var pos = new Dictionary<SmbiosSlot, int>();
            foreach (var s in bd.Slots)
            {
                int p;
                if (slotOverride.TryGetValue(s.Name.Replace(' ', '_'), out p)) { pos[s] = p; free.Remove(p); }
            }
            Func<int, int> take = want =>
            {
                if (free.Count == 0) return positions + pos.Count;
                int best = free.OrderBy(f => Math.Abs(f - want)).First();
                free.Remove(best);
                return best;
            };
            var big = bd.Slots.Where(s => s.PhysLanes >= 8 && !pos.ContainsKey(s)).OrderBy(s => s.Name).ToList();
            for (int k = 0; k < big.Count; k++)
            {
                int want = k == 0 ? (positions > 1 ? 1 : 0)
                         : k == big.Count - 1 ? positions - 1
                         : 1 + (int)Math.Round(k * (positions - 2.0) / (big.Count - 1));
                pos[big[k]] = take(want);
            }
            var rest = bd.Slots.Where(s => !pos.ContainsKey(s)).OrderBy(s => s.Name).ToList();
            if (rest.Count > 0 && free.Contains(0)) { pos[rest[0]] = 0; free.Remove(0); rest.RemoveAt(0); }
            var freeList = free.ToList();
            int start = Math.Max(0, freeList.Count - rest.Count);
            for (int k = 0; k < rest.Count; k++)
                pos[rest[k]] = start + k < freeList.Count ? freeList[start + k] : positions + k;

            var occupant = new Dictionary<SmbiosSlot, DeviceInfo>();
            foreach (var s in bd.Slots)
            {
                if (s.Bus < 0) continue;
                var b = devs.FirstOrDefault(d => d.PciBus == s.Bus && d.PciDev == s.Dev && d.PciFn == s.Fn);
                if (b == null) continue;
                var e = Endpoint(b, kids);
                if (e != null) { occupant[s] = e; usedDevs.Add(e); }
            }
            var guessed = new HashSet<SmbiosSlot>();
            foreach (var s in bd.Slots.Where(s => s.Usage == 4 && !occupant.ContainsKey(s) && s.PhysLanes >= 16).OrderBy(s => pos[s]))
            {
                var gpu = devs.FirstOrDefault(d => IsPci(d) && d.Class == "Display" && !usedDevs.Contains(d));
                if (gpu != null) { occupant[s] = gpu; usedDevs.Add(gpu); guessed.Add(s); }
            }

            bool firstBig = true;
            foreach (var s in bd.Slots.OrderBy(s => pos[s]))
            {
                int p = pos[s];
                double y = slotY0 + p * Pitch;
                int phys = s.PhysLanes;
                double len = phys >= 16 ? 89 : phys >= 8 ? 56 : phys >= 4 ? 39 : 25;
                bool steel = phys >= 16 && firstBig;
                if (phys >= 16) firstBig = false;
                DeviceInfo occ;
                occupant.TryGetValue(s, out occ);
                var sp = Part(L, "slot", s.Name, s.Name + "  (PCIe x" + phys + " slot)", 40, y - 3.5, len, 7, 0, 11, steel ? 0xFFB9BDC4 : 0xFF33363B, s.Name);
                sp.Pos = p;
                sp.Shiny = steel;
                if (steel) sp.TextColor = 0xFF202226;
                sp.Estimated = !slotOverride.ContainsKey(s.Name.Replace(' ', '_'));
                var g = sp.G("PCI Express slot");
                g.Add("Slot", s.Name);
                g.Add("Physical size", "x" + phys);
                if (s.ElecLanes > 0) g.Add("Electrical lanes", "x" + s.ElecLanes);
                g.Add("Status", s.Usage == 4 ? "In use" : s.Usage == 3 ? "Empty" : "Unknown");
                g.Add("Position", "#" + (p + 1) + " from the top" + (sp.Estimated ? " (inferred - adjust in the layout file)" : " (from layout profile)"));
                if (s.Bus >= 0) g.Add("Slot PCI address", "bus " + s.Bus + ", device " + s.Dev + ", function " + s.Fn);
                if (occ != null)
                {
                    sp.Details.Add(DeviceGroup(occ, guessed.Contains(s) ? "Installed device (best guess)" : "Installed device"));
                    if (occ.Class == "Display")
                    {
                        var gg = FindGroup(pages, "Graphics", x => x.Name == occ.Name);
                        if (gg != null) sp.Details.Add(gg);
                    }
                    if (occ.Class == "Net")
                    {
                        var ng = FindGroup(pages, "Network", x => x.Name.StartsWith(occ.Name));
                        if (ng != null) sp.Details.Add(ng);
                    }
                    bool isGpu = occ.Class == "Display";
                    BoardComponent card;
                    if (isGpu)
                    {
                        bool large = Regex.IsMatch(occ.Name, @"RTX|RX\s?[5-9]\d{3}|Arc|TITAN|Radeon Pro|Quadro", RegexOptions.IgnoreCase);
                        card = Part(L, "card", s.Name + "_CARD", occ.Name, -10, y - 6, large ? 285 : 210, large ? 54 : 40, 4, large ? 122 : 105, 0xFF2B2E33, Shorten(occ.Name, 30));
                    }
                    else
                        card = Part(L, "card", s.Name + "_CARD", occ.Name, -10, y - 3, Math.Max(len + 20, 100), 8, 4, 64, 0xFF1E4D2B, Shorten(occ.Name, 22));
                    card.IsCard = true;
                    card.Anchor = sp.Key;
                    card.Details = sp.Details;
                    card.Estimated = sp.Estimated;
                }
                else if (s.Usage == 4)
                    g.Add("Installed device", "Card present, but Windows did not report which device it is");
            }

            // ---- connectors from SMBIOS
            var conns = bd.Connectors.ToList();
            Func<Connector, bool> isM2 = c => Regex.IsMatch(c.Name, @"^M\.?2", RegexOptions.IgnoreCase);
            Func<Connector, bool> isWifiM2 = c => isM2(c) && (c.PortType == 31 || Regex.IsMatch(c.Name, "WIFI|WLAN|E.?KEY", RegexOptions.IgnoreCase));
            var m2s = conns.Where(c => !c.Rear && isM2(c) && !isWifiM2(c)).OrderBy(c => c.Name).ToList();
            var wifiM2 = conns.FirstOrDefault(c => !c.Rear && isWifiM2(c));
            var satas = conns.Where(c => !c.Rear && !isM2(c) && (c.PortType == 32 || Regex.IsMatch(c.Name, "SATA", RegexOptions.IgnoreCase))).OrderBy(c => TrailingNumber(c.Name)).ToList();

            // ---- M.2 sockets
            var nvme = devs.Where(d => IsPci(d) && !usedDevs.Contains(d) && (d.Class == "SCSIAdapter" || d.Class == "HDC") &&
                                        (d.Name.IndexOf("NVM", StringComparison.OrdinalIgnoreCase) >= 0 || (d.HardwareIds ?? "").IndexOf("CC_0108", StringComparison.OrdinalIgnoreCase) >= 0))
                           .OrderBy(d => Depth(d, byId)).ThenBy(d => d.PciBus).ToList();
            double lastM2Bottom = 0;
            for (int k = 0; k < m2s.Count; k++)
            {
                double mx, my;
                if (k == 0) { mx = small ? 14 : 55; my = m2Y0; }
                else { mx = small ? 14 : W - 118; my = small ? m2Y0 + k * 26 : slotY0 + 15 + (k - 1) * 28; }
                lastM2Bottom = Math.Max(lastM2Bottom, my + 22);
                var c = m2s[k];
                var ctrl = k < nvme.Count ? nvme[k] : null;
                var disk = ctrl == null ? null : bd.Disks.FirstOrDefault(d => d.ParentId.Equals(ctrl.DeviceId, StringComparison.OrdinalIgnoreCase));
                var sp = Part(L, "m2", c.Name, c.Name + (ctrl == null ? " (empty)" : ""), mx, my, 80, 22, 0, 1.5, 0xFF30353C, ctrl == null ? c.Name : "");
                sp.Estimated = true;
                var g = sp.G("M.2 socket");
                g.Add("Socket", c.Name);
                g.Add("Status", ctrl == null ? "Empty (or SATA M.2 drive)" : "NVMe SSD installed");
                if (ctrl != null)
                {
                    g.Add("Lanes from", Depth(ctrl, byId) <= 1 ? "CPU (direct-attached root port)" : "Chipset");
                    g.Add("Mapping", "Matched by PCI topology order (verify with the board manual)");
                    if (disk != null && disk.Group != null) sp.Details.Add(disk.Group);
                    sp.Details.Add(DeviceGroup(ctrl, "NVMe controller"));
                    var ssd = Part(L, "ssd", c.Name + "_SSD", disk != null ? disk.Model + " (" + disk.Size + ")" : ctrl.Name, mx + 1, my + 2, 78, 18, 1.5, 2.6, 0xFF101114,
                                   disk != null ? Shorten(disk.Model, 24) : "NVMe");
                    ssd.Anchor = sp.Key;
                    ssd.Details = sp.Details;
                    usedDevs.Add(ctrl);
                }
            }
            if (nvme.Count > m2s.Count)
                L.Notes.Add((nvme.Count - m2s.Count) + " NVMe controller(s) could not be placed on an M.2 socket (adapter card or unlisted socket)");

            // ---- M.2 Wi-Fi socket
            var wireless = devs.FirstOrDefault(d => IsPci(d) && d.Class == "Net" && !usedDevs.Contains(d) && Regex.IsMatch(d.Name, "Wi-?Fi|Wireless|WLAN|802\\.11", RegexOptions.IgnoreCase));
            if (wifiM2 != null)
            {
                var wp = Part(L, "m2", wifiM2.Name, wifiM2.Name + (wireless == null ? " (empty)" : ""), 20, slotY0 - 24, 22, 12, 0, 1.5, 0xFF30353C, "WiFi");
                var g = wp.G("M.2 Key-E socket (Wi-Fi / Bluetooth)");
                g.Add("Socket", wifiM2.Name);
                g.Add("Status", wireless == null ? "Empty" : "Module installed");
                if (wireless != null)
                {
                    wp.Details.Add(DeviceGroup(wireless, "Wireless module"));
                    var mod = Part(L, "card", wifiM2.Name + "_MODULE", wireless.Name, 21, slotY0 - 23, 20, 10, 1.5, 2.2, 0xFF1E4D2B, "");
                    mod.Anchor = wp.Key;
                    mod.Details = wp.Details;
                    usedDevs.Add(wireless);
                }
            }

            // ---- SATA
            double sataY0 = small ? 100 : slotY0 + 20;
            var sataDisks = bd.Disks.Where(d =>
            {
                DeviceInfo pd;
                return byId.TryGetValue(d.ParentId, out pd) && (pd.Class == "HDC" || Regex.IsMatch(pd.Name, "AHCI|SATA", RegexOptions.IgnoreCase)) && d.ScsiBus >= 0;
            }).ToList();
            var placed = new HashSet<DiskRef>();
            for (int i = 0; i < satas.Count; i++)
            {
                var c = satas[i];
                int num = TrailingNumber(c.Name);
                var disk = sataDisks.FirstOrDefault(d => d.ScsiBus + 1 == num);
                var sp = Part(L, "sata", c.Name, c.Name + (disk != null ? " - " + disk.Model : " (empty)"), W - 18, sataY0 + i * 12, 14, 10, 0, 9,
                              disk != null ? 0xFFB3261E : 0xFF26292E, num > 0 ? num.ToString() : "");
                var g = sp.G("SATA port");
                g.Add("Port", c.Name);
                g.Add("Status", disk != null ? "Drive connected" : "Nothing detected");
                if (disk != null)
                {
                    placed.Add(disk);
                    sp.Estimated = true;
                    g.Add("Mapping", "From AHCI port number " + disk.ScsiBus + " (estimated; boards may number ports differently)");
                    if (disk.Group != null) sp.Details.Add(disk.Group);
                }
            }
            var unplaced = sataDisks.Where(d => !placed.Contains(d)).ToList();
            if (unplaced.Count > 0) L.Notes.Add("SATA drive(s) not matched to a port: " + string.Join(", ", unplaced.Select(d => d.Model)));

            // ---- rear I/O
            var rear = conns.Where(c => c.Rear).ToList();
            double ry = small ? 8 : 10, avail = (small ? slotY0 - 20 : slotY0 - 14) - ry;
            Func<Connector, double[]> size = c =>
            {
                if (c.PortType == 16) return Regex.IsMatch(c.Name, @"C\d|TYPE.?C|_C", RegexOptions.IgnoreCase) ? new[] { 9.0, 5.0 } : new[] { 15.0, 15.0 };
                if (c.PortType == 31) return new[] { 16.0, 14.0 };
                if (c.PortType == 28) return new[] { 15.0, 8.0 };
                if (c.PortType == 29) return new[] { 26.0, 28.0 };
                return new[] { 12.0, 10.0 };
            };
            double total = rear.Sum(c => size(c)[0] + 3);
            double scale = total > avail ? avail / total : 1;
            foreach (var c in rear)
            {
                var sz = size(c);
                uint col = 0xFF2A2A2A;
                string kind = "Port";
                if (c.PortType == 16)
                {
                    kind = "USB";
                    if (Regex.IsMatch(c.Name, "G2|3\\.2|3\\.1|U31", RegexOptions.IgnoreCase)) col = 0xFFB3261E;
                    else if (Regex.IsMatch(c.Name, "U3|USB3|G1", RegexOptions.IgnoreCase)) col = 0xFF1A5FB4;
                    else col = 0xFF1A1A1A;
                }
                else if (c.PortType == 31) { kind = "Network (RJ-45)"; col = 0xFF9EA3A8; }
                else if (c.PortType == 28) { kind = "Video output"; col = 0xFF1A1A1A; }
                else if (c.PortType == 29) { kind = "Audio jacks"; col = 0xFF2E7D32; }
                var p = Part(L, "port", c.Name, c.External + "  (rear I/O)", -6, ry, 24, sz[0] * scale, 2, sz[1], col, c.Internal);
                p.TextColor = c.PortType == 31 ? 0xFF1A1A1A : 0xFFFFFFFF;
                ry += (sz[0] + 3) * scale;
                var g = p.G("Rear I/O connector");
                g.Add("Connector", c.External);
                g.Add("Internal name", c.Internal);
                g.Add("Type", kind);
                if (c.PortType == 16) AddUsbList(p, devs);
                if (c.PortType == 31)
                {
                    var lan = devs.FirstOrDefault(d => IsPci(d) && d.Class == "Net" && !usedDevs.Contains(d) && !Regex.IsMatch(d.Name, "Wi-?Fi|Wireless|WLAN", RegexOptions.IgnoreCase));
                    if (lan != null)
                    {
                        var ng = FindGroup(pages, "Network", x => x.Name.StartsWith(lan.Name));
                        if (ng != null) p.Details.Add(ng);
                    }
                }
                if (c.PortType == 29) foreach (var ag in PageGroups(pages, "Audio")) p.Details.Add(ag);
                if (c.PortType == 28)
                {
                    bool igpu = devs.Any(d => IsPci(d) && d.Class == "Display" && !usedDevs.Contains(d));
                    g.Add("Driven by", igpu ? "CPU integrated graphics" : "CPU integrated graphics - none active (CPU has no iGPU or it is disabled), so this output is unused");
                }
            }

            // ---- internal headers
            var fans = new List<Connector>();
            var usb3 = new List<Connector>();
            var bottom = new List<Connector>();
            foreach (var c in conns.Where(c => !c.Rear && !isM2(c) && !satas.Contains(c)))
            {
                if (Regex.IsMatch(c.Name, "FAN|PUMP|OPT", RegexOptions.IgnoreCase)) fans.Add(c);
                else if (c.PortType == 16 && Regex.IsMatch(c.Name, "U3|USB3|U32|USB_?C", RegexOptions.IgnoreCase)) usb3.Add(c);
                else bottom.Add(c);
            }
            int topK = 0, rightK = 0;
            foreach (var c in fans)
            {
                bool cpuish = Regex.IsMatch(c.Name, "CPU|PUMP|AIO|OPT", RegexOptions.IgnoreCase);
                BoardComponent p;
                if (cpuish && topK < 4) p = Part(L, "fan", c.Name, c.Name + " (fan header)", dimmX0 + (topK++) * 12, 3, 10, 6, 0, 8, 0xFFE0E0E0, "");
                else if (rightK < 4) p = Part(L, "fan", c.Name, c.Name + " (fan header)", W - 9, (small ? 10 : 18) + (rightK++) * 15, 7, 10, 0, 8, 0xFFE0E0E0, "");
                else { bottom.Add(c); continue; }
                p.TextColor = 0xFF202020;
                var g = p.G("Fan / pump header");
                g.Add("Header", c.Name);
                g.Add("Type", "4-pin PWM fan header");
                g.Add("Note", "Fan RPM needs a hardware-sensor driver; Windows WMI does not report it");
            }
            for (int k = 0; k < usb3.Count; k++)
            {
                var c = usb3[k];
                var p = Part(L, "header", c.Name, c.Name + " (internal USB header)", W - 11, (small ? 88 : 148) + k * 26, 9, 22, 0, 9, 0xFF1A5FB4, "");
                var g = p.G("Internal USB header");
                g.Add("Header", c.Name);
                g.Add("Used for", "Front-panel / case USB ports");
                AddUsbList(p, devs);
            }
            if (bottom.Count > 0)
            {
                double step = (W - 28) / bottom.Count, w = Math.Min(20, step - 3);
                for (int k = 0; k < bottom.Count; k++)
                {
                    var c = bottom[k];
                    var p = Part(L, "header", c.Name, c.Name + " (header)", 14 + k * step, Hh - 12, w, 8, 0, 8, 0xFF1A1A1A, c.Name);
                    var g = p.G("On-board header");
                    g.Add("Header", c.Name);
                    g.Add("Type", HeaderType(c));
                    if (c.PortType == 16) AddUsbList(p, devs);
                }
            }

            // ---- chipset
            var cm2 = Regex.Match(bd.Product, @"\b([ABHXZQW]\d{3}[EM]?|[ABHXZQW]\d{2}[EM]?)\b");
            string chipName = cm2.Success ? cm2.Groups[1].Value : "";
            double chipY = Math.Min(Hh - 46, Math.Max(Hh - 75, lastM2Bottom + 6));
            var chip = Part(L, "chip", "CHIPSET", "Chipset" + (chipName != "" ? " (" + (bd.CpuVendor != "" ? bd.CpuVendor + " " : "") + chipName + ")" : ""),
                            W - 62, chipY, 32, 32, 0, 10, 0xFF50565E, chipName != "" ? chipName : "PCH");
            chip.Shiny = true;
            var cg = FindGroup(pages, "Mainboard", g => g.Name == "Chipset");
            if (cg != null) chip.Details.Add(cg);
            var onboard = devs.Where(d => IsPci(d) && !usedDevs.Contains(d) && !IsBridge(d) && !Regex.IsMatch(d.Name, "host CPU bridge|^AMD PCI$", RegexOptions.IgnoreCase)).ToList();
            if (onboard.Count > 0)
            {
                var g = chip.G("On-board PCI devices (CPU + chipset)");
                foreach (var d in onboard.OrderBy(d => d.Name)) g.Add(d.Name, H.ClassName(d.Class) + "  ·  " + H.Ids(d.DeviceId));
            }

            // ---- on-board controllers
            var lanChip = devs.FirstOrDefault(d => IsPci(d) && d.Class == "Net" && !usedDevs.Contains(d) && !Regex.IsMatch(d.Name, "Wi-?Fi|Wireless|WLAN", RegexOptions.IgnoreCase));
            if (lanChip != null)
            {
                var p = Part(L, "chip", "LAN_CHIP", lanChip.Name, small ? 10 : 52, small ? 90 : 104, 11, 11, 0, 1.5, 0xFF111214, "LAN");
                var ng = FindGroup(pages, "Network", x => x.Name.StartsWith(lanChip.Name));
                if (ng != null) p.Details.Add(ng);
                p.Details.Add(DeviceGroup(lanChip, "Controller"));
            }
            var codec = devs.FirstOrDefault(d => d.DeviceId.StartsWith(@"HDAUDIO\FUNC_01", StringComparison.OrdinalIgnoreCase) &&
                                                  !Regex.IsMatch(d.DeviceId, "VEN_(1002|10DE|8086|1AF4)", RegexOptions.IgnoreCase));
            if (codec != null)
            {
                var p = Part(L, "chip", "AUDIO_CODEC", codec.Name + " (audio codec)", 14, Hh - 48, 14, 14, 0, 1.6, 0xFF111214, "Audio");
                p.Details.Add(DeviceGroup(codec, "Audio codec"));
                foreach (var ag in PageGroups(pages, "Audio")) p.Details.Add(ag);
            }
            var bat = Part(L, "battery", "BATTERY", "CMOS battery (CR2032)", small ? W - 60 : 150, small ? Hh - 40 : Hh - 55, 20, 20, 0, 3.2, 0xFFC8CBD0, "CR2032");
            bat.Cylinder = true;
            bat.Shiny = true;
            bat.TextColor = 0xFF303030;
            bat.G("CMOS battery").Add("Purpose", "Keeps the real-time clock and BIOS settings while the PC is unplugged");

            // ---- profile moves
            foreach (var mv in moves)
            {
                var p = L.Parts.FirstOrDefault(x => x.Key.Equals(mv.Item1, StringComparison.OrdinalIgnoreCase));
                if (p == null) continue;
                double dx = mv.Item2 - p.X, dy = mv.Item3 - p.Y;
                p.X += dx; p.Y += dy;
                p.Estimated = false;
                foreach (var a in L.Parts.Where(x => x.Anchor.Equals(p.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    a.X += dx; a.Y += dy; a.Estimated = false;
                }
            }

            // ---- notes
            L.Notes.Insert(0, "Form factor: " + L.FormFactor + " (estimated from " + nSlots + " expansion slots and " + bd.MemSlots + " memory slots)");
            L.Notes.Add("Slot, port and header names come from the board's own SMBIOS data; their positions follow standard " + L.FormFactor + " geometry and naming conventions, so they are approximate");
            L.Notes.Add("Devices in PCIe slots are matched through the real PCI topology (slot bus address -> bridges -> card)");
            L.Notes.Add("Windows does not report which physical USB port a device is plugged into");
            L.Notes.Add(L.ProfileLoaded ? "Layout profile loaded: " + L.ProfilePath : "Fine-tune positions: 'Save layout file' writes " + L.ProfilePath + " - edit it, then Refresh");
            var ng2 = new InfoGroup { Name = "About this 3D view" };
            foreach (var n in L.Notes) ng2.Add("note", n);
            pcb.Details.Add(ng2);
            return L;
        }

        static string HeaderType(Connector c)
        {
            string n = c.Name.ToUpperInvariant();
            if (c.PortType == 16) return "USB 2.0 header";
            if (c.PortType == 29 || n.Contains("AAFP")) return "Front-panel audio";
            if (n.Contains("PANEL") || n.Contains("F_PANEL") || n.Contains("JFP")) return "Front-panel (power / reset / LEDs)";
            if (n.Contains("RGB") || n.Contains("ADD_GEN") || n.Contains("ARGB")) return "RGB / addressable RGB lighting";
            if (n.Contains("TPM")) return "TPM module header";
            if (n.Contains("COM") || c.PortType == 9) return "Serial (COM) port header";
            if (n.Contains("SENSOR") || n.Contains("T_")) return "Thermal sensor header";
            if (n.Contains("TB")) return "Thunderbolt add-in card header";
            if (c.PortType == 0 || c.PortType == 255) return "Other";
            return "Port type " + c.PortType;
        }

        static void AddUsbList(BoardComponent p, List<DeviceInfo> devs)
        {
            var usb = devs.Where(d => d.DeviceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) &&
                                      d.DeviceId.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) < 0 &&
                                      d.Name.IndexOf("hub", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            var g = p.G("USB devices currently connected (any port)");
            foreach (var d in usb.OrderBy(d => d.Name)) g.Add(d.Name, H.Ids(d.DeviceId));
            g.Add("note", "Windows does not expose the physical port each device uses");
        }

        public static void WriteTemplate(BoardLayout L)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(L.ProfilePath));
            var sb = new StringBuilder();
            sb.AppendLine("# HW Inspector board layout for: " + L.Title + " (" + L.FormFactor + ", " + L.Width + " x " + L.Height + " mm)");
            sb.AppendLine("# Coordinates in mm. X = distance from the rear I/O edge, Y = distance from the top edge.");
            sb.AppendLine("#   slot <SLOT_NAME> <position>   position 1 = top-most expansion slot position");
            sb.AppendLine("#   move <KEY> <x> <y>            move a part (anything sitting on it moves too)");
            sb.AppendLine("# Remove the leading '#' from a line to activate it, then press Refresh in the app.");
            sb.AppendLine();
            foreach (var p in L.Parts.Where(p => p.Kind == "slot").OrderBy(p => p.Pos))
                sb.AppendLine("slot " + p.Key + " " + (p.Pos + 1));
            sb.AppendLine();
            foreach (var p in L.Parts.Where(p => p.Anchor == "" && p.Kind != "pcb"))
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "# move {0} {1:0.#} {2:0.#}", p.Key, p.X, p.Y));
            File.WriteAllText(L.ProfilePath, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
