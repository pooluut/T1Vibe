# Run in Windows PowerShell: .\key-probe.ps1
# Exits automatically after 20 seconds. Only use while testing the remote; keyboard key codes are printed.
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public sealed class KeyProbe : Form {
    const uint INPUTSINK = 0x100, RID_INPUT = 0x10000003, RID_DEVICEINFO = 0x2000000b;
    const int WM_INPUT = 0xff, WM_APPCOMMAND = 0x319, WH_KEYBOARD_LL = 13;
    [StructLayout(LayoutKind.Sequential)] struct RawDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct DeviceList { public IntPtr Handle; public uint Type; }
    [StructLayout(LayoutKind.Sequential)] struct Kbd { public uint Vk, Scan, Flags, Time; public IntPtr Extra; }
    delegate IntPtr KeyboardProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError=true)] static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError=true)] static extern uint GetRawInputDeviceList([Out] DeviceList[] devices, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError=true)] static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);
    [DllImport("user32.dll", SetLastError=true)] static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SetWindowsHookEx(int id, KeyboardProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    KeyboardProc callback;
    IntPtr hook;
    Timer timeout;
    public KeyProbe() {
        ShowInTaskbar = false; WindowState = FormWindowState.Minimized; Opacity = 0;
        Text = "T1 key probe";
        Shown += delegate { Hide(); };
        var handle = Handle;
        callback = OnKey;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, callback, IntPtr.Zero, 0);
        if (hook == IntPtr.Zero) Console.WriteLine("KEY hook failed: " + Marshal.GetLastWin32Error());
        RegisterHidCollections(handle);
        timeout = new Timer(); timeout.Interval = 20000; timeout.Tick += delegate { timeout.Stop(); Console.WriteLine("Done. Probe stopped."); Close(); }; timeout.Start();
        Console.WriteLine("Ready. Press the voice key once; probe stops automatically after 20 seconds.");
    }
    void RegisterHidCollections(IntPtr handle) {
        uint count = 0;
        uint entrySize = (uint)Marshal.SizeOf(typeof(DeviceList));
        GetRawInputDeviceList(null, ref count, entrySize);
        var list = new DeviceList[count];
        if (count > 0 && GetRawInputDeviceList(list, ref count, entrySize) == uint.MaxValue) {
            Console.WriteLine("HID enumeration failed: " + Marshal.GetLastWin32Error()); return;
        }
        var collections = new Dictionary<string,RawDevice>();
        foreach (var item in list) {
            if (item.Type != 2) continue;
            uint size = 0;
            GetRawInputDeviceInfo(item.Handle, RID_DEVICEINFO, IntPtr.Zero, ref size);
            if (size < 24 || size > 256) continue;
            IntPtr info = Marshal.AllocHGlobal((int)size);
            try {
                Marshal.WriteInt32(info, (int)size);
                if (GetRawInputDeviceInfo(item.Handle, RID_DEVICEINFO, info, ref size) == uint.MaxValue) continue;
                ushort page = (ushort)Marshal.ReadInt16(info, 20), usage = (ushort)Marshal.ReadInt16(info, 22);
                // Raw keyboard and mouse classes are covered by the low-level hook.
                if (page == 1 && (usage == 2 || usage == 6)) continue;
                collections[page + ":" + usage] = new RawDevice { Page = page, Usage = usage, Flags = INPUTSINK, Target = handle };
            } finally { Marshal.FreeHGlobal(info); }
        }
        collections["12:1"] = new RawDevice { Page = 12, Usage = 1, Flags = INPUTSINK, Target = handle };
        foreach (var pair in collections) {
            var d = pair.Value;
            if (RegisterRawInputDevices(new [] { d }, 1, (uint)Marshal.SizeOf(typeof(RawDevice))))
                Console.WriteLine("Listening HID page=0x{0:X4} usage=0x{1:X4}", d.Page, d.Usage);
            else Console.WriteLine("HID registration failed page=0x{0:X4} usage=0x{1:X4} error={2}", d.Page, d.Usage, Marshal.GetLastWin32Error());
        }
    }
    IntPtr OnKey(int code, IntPtr message, IntPtr data) {
        if (code >= 0 && (message.ToInt64() == 0x100 || message.ToInt64() == 0x104)) {
            var k = (Kbd)Marshal.PtrToStructure(data, typeof(Kbd));
            if ((k.Flags & 0x10) == 0) Console.WriteLine("KEY vk=0x{0:X2} scan=0x{1:X2}", k.Vk, k.Scan);
        }
        return CallNextHookEx(hook, code, message, data);
    }
    protected override void WndProc(ref Message m) {
        if (m.Msg == WM_APPCOMMAND) Console.WriteLine("APPCOMMAND code={0}", (m.LParam.ToInt64() >> 16) & 0xfff);
        if (m.Msg == WM_INPUT) {
            uint size = 0, header = (uint)(8 + 2 * IntPtr.Size);
            GetRawInputData(m.LParam, RID_INPUT, IntPtr.Zero, ref size, header);
            if (size >= header + 8 && size < 65536) {
                IntPtr data = Marshal.AllocHGlobal((int)size);
                try {
                    if (GetRawInputData(m.LParam, RID_INPUT, data, ref size, header) != uint.MaxValue && Marshal.ReadInt32(data) == 2) {
                        int reportSize = Marshal.ReadInt32(data, (int)header), count = Marshal.ReadInt32(data, (int)header + 4);
                        if (reportSize > 0 && count > 0 && (long)reportSize * count <= size - header - 8) {
                            for (int i = 0; i < Math.Min(count, 8); i++) {
                                byte[] report = new byte[Math.Min(reportSize, 32)];
                                Marshal.Copy(IntPtr.Add(data, (int)header + 8 + i * reportSize), report, 0, report.Length);
                                Console.WriteLine("HID device=0x{0:X} size={1} data={2}", Marshal.ReadIntPtr(data, 8).ToInt64(), reportSize, BitConverter.ToString(report));
                            }
                        }
                    }
                } finally { Marshal.FreeHGlobal(data); }
            }
        }
        base.WndProc(ref m);
    }
    protected override void Dispose(bool disposing) {
        if (timeout != null) { timeout.Stop(); timeout.Dispose(); timeout = null; }
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        base.Dispose(disposing);
    }
}
'@
[System.Windows.Forms.Application]::Run((New-Object KeyProbe))
