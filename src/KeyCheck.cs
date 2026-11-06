using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Standalone receiver-side diagnostic. It never sends keys or starts dictation.
sealed class KeyCheck : Form
{
    const uint RidInput = 0x10000003, RidInfo = 0x2000000b, RidName = 0x20000007, RidPreparsed = 0x20000005;
    readonly string[] names = { "应用图标 / Apps", "主页", "上", "下", "确认 / Enter", "返回", "语音" };
    readonly Result[] results = new Result[7];
    readonly Dictionary<IntPtr, Device> devices = new Dictionary<IntPtr, Device>();
    readonly Queue<Signal> pending = new Queue<Signal>();
    readonly object pendingLock = new object();
    readonly HashSet<string> registrations = new HashSet<string>();
    readonly StringBuilder trace = new StringBuilder();
    readonly Label prompt = new Label(), summary = new Label(), environment = new Label();
    readonly DataGridView grid = new DataGridView();
    readonly TextBox events = new TextBox();
    readonly Button start = new Button(), single = new Button(), stop = new Button(), save = new Button();
    readonly Timer timer = new Timer();
    readonly Stopwatch clock = new Stopwatch();
    readonly string reportPath = Path.Combine(Path.GetTempPath(), "T1Vibe-key-check-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
    HookProc callback;
    IntPtr hook;
    uint shellMessage;
    int phase = -1, lastSecond = -1;
    bool sequence, capturing, listening, healthy = true;
    string desktop;

    [STAThread] static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--self-test") >= 0) { SelfTest(); return; }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new KeyCheck());
    }

    KeyCheck()
    {
        Text = "T1Vibe 按键接收验证";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(980, 760); MinimumSize = new Size(850, 650);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 255));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "在需要接收遥控器按键的电脑上运行。退出本机 T1Vibe 接收端后点击开始。\r\n按提示逐键短按；语音同时识别 HID 和转发的 F9。仅测试期间记录键码，不录音。", AutoSize = false }, 0, 0);
        prompt.Dock = DockStyle.Fill; prompt.Font = new Font(Font, FontStyle.Bold); prompt.ForeColor = Color.DarkBlue;
        prompt.Text = "尚未开始。请将本窗口保持在远程桌面前台。"; layout.Controls.Add(prompt, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        start.Text = "开始逐键验证"; single.Text = "重测选中键"; stop.Text = "停止采集"; save.Text = "打开结果文件";
        foreach (var button in new[] { start, single, stop, save }) { button.AutoSize = true; buttons.Controls.Add(button); }
        stop.Enabled = false;
        start.Click += delegate { BeginTest(0, true); };
        single.Click += delegate { BeginTest(grid.CurrentRow == null ? 6 : grid.CurrentRow.Index, false); };
        stop.Click += delegate { Finish(false); };
        save.Click += delegate { WriteReport(); Process.Start("notepad.exe", "\"" + reportPath + "\""); };
        layout.Controls.Add(buttons, 0, 2);
        grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false; grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.Columns.Add("key", "本轮要求按的键"); grid.Columns.Add("result", "接收结果"); grid.Columns.Add("detail", "匹配到的编码 / 通道");
        grid.Columns[0].FillWeight = 22; grid.Columns[1].FillWeight = 28; grid.Columns[2].FillWeight = 50;
        for (int i = 0; i < names.Length; i++) { results[i] = new Result(); grid.Rows.Add(names[i], "待测", ""); grid.Rows[i].Height = 30; }
        layout.Controls.Add(grid, 0, 3);
        summary.Dock = DockStyle.Fill; summary.Text = "结果只表示本机收到的信号；普通键盘也可产生同样的键码。"; layout.Controls.Add(summary, 0, 4);
        events.Dock = DockStyle.Fill; events.Multiline = true; events.ReadOnly = true; events.ScrollBars = ScrollBars.Both; events.WordWrap = false;
        layout.Controls.Add(events, 0, 5);
        environment.Dock = DockStyle.Fill; layout.Controls.Add(environment, 0, 6);
        Controls.Add(layout);
        timer.Interval = 50; timer.Tick += delegate { TickCapture(); };
        Shown += delegate
        {
            desktop = DesktopName();
            environment.Text = "桌面：" + desktop + "；会话：" + Process.GetCurrentProcess().SessionId;
            Log("START machine=" + Environment.MachineName + " desktop=" + desktop + " session=" + Process.GetCurrentProcess().SessionId);
            Log("Report: " + reportPath);
            if (desktop.IndexOf("Default", StringComparison.OrdinalIgnoreCase) < 0)
                prompt.Text = "当前不在 Default 交互桌面，测试结果可能看不到实际按键。请从实际远程桌面双击此程序。";
            WriteReport();
        };
        FormClosed += delegate { StopListening(); timer.Dispose(); foreach (var device in devices.Values) device.Dispose(); WriteReport(); };
    }

    void BeginTest(int index, bool all)
    {
        if (capturing) return;
        if (Process.GetProcessesByName("T1Vibe").Length > 0 || Process.GetProcessesByName("T1VibeUU").Length > 0)
        { MessageBox.Show(this, "请先从托盘退出本机 T1Vibe / T1VibeUU，再开始测试，避免按键被接收端截走。另一台电脑上的转发端可以继续运行。", Text); return; }
        sequence = all; healthy = string.Equals(desktop, "Default", StringComparison.OrdinalIgnoreCase);
        if (all) for (int i = 0; i < results.Length; i++) { results[i] = new Result(); UpdateRow(i); }
        StartListening();
        if (hook == IntPtr.Zero) { prompt.Text = "键盘监听失败，不能据此判断按键未收到；请在实际远程桌面运行。"; StopListening(); WriteReport(); return; }
        capturing = true; start.Enabled = single.Enabled = save.Enabled = false; stop.Enabled = true;
        StartPhase(index); timer.Start();
    }
    void StartPhase(int index)
    {
        phase = index; results[index] = new Result(); grid.ClearSelection(); grid.Rows[index].Selected = true;
        Log("PHASE_BEGIN " + names[index] + " duration=" + Duration(index) + "s");
        clock.Restart(); lastSecond = -1; UpdateRow(index); UpdatePrompt();
    }
    static int Duration(int index) { return index == 6 ? 12 : 8; }
    void UpdatePrompt()
    {
        int remaining = Math.Max(0, Duration(phase) - (int)clock.Elapsed.TotalSeconds);
        if (remaining == lastSecond) return; lastSecond = remaining;
        prompt.Text = "现在请短按【" + names[phase] + "】键，其他键暂时不要按。   剩余 " + remaining + " 秒\r\n" +
            (results[phase].Matches > 0 ? "已收到匹配事件；请等待下一项。" : "正在采集，包括远控注入的按键和未知 HID 编码……");
    }
    void TickCapture()
    {
        Drain(); if (!capturing) return;
        UpdatePrompt();
        if (clock.Elapsed.TotalSeconds < Duration(phase)) return;
        results[phase].Completed = true; UpdateRow(phase); Log("PHASE_END " + names[phase] + " " + results[phase].Status(healthy)); WriteReport();
        if (sequence && phase + 1 < names.Length) StartPhase(phase + 1); else Finish(true);
    }
    void Finish(bool completed)
    {
        if (!capturing) return;
        Drain(); capturing = false; timer.Stop(); clock.Stop(); StopListening();
        if (!completed) { results[phase].Interrupted = true; UpdateRow(phase); }
        start.Enabled = single.Enabled = save.Enabled = true; stop.Enabled = false;
        prompt.Text = completed ? "本轮验证结束。可选中“语音”后点击“重测选中键”再次确认。" : "采集已停止；未完成项目不能用于判断未收到。";
        int other = 0, tested = 0; for (int i = 0; i < 6; i++) { if (results[i].Completed) tested++; if (results[i].Matches > 0) other++; }
        summary.Text = "其他功能键：" + other + "/6 项匹配（完成 " + tested + " 项）；语音：" + results[6].Status(healthy) + "。\r\n" +
            "只有按提示实际按过，未见事件才有比较意义。未知事件请查看下方编码；结果已保存到临时目录。";
        Log("FINISH " + summary.Text.Replace("\r\n", " ")); WriteReport();
    }
    void Record(string channel, string detail, int key)
    {
        if (!capturing) return;
        results[phase].Events++;
        if (key == phase) { results[phase].Matches++; results[phase].Detail = channel + " " + detail; }
        Log("EVENT phase=" + names[phase] + " channel=" + channel + " " + detail + " mapped=" + (key < 0 ? "unknown" : names[key]));
        UpdateRow(phase);
    }
    void UpdateRow(int index)
    {
        var result = results[index]; grid.Rows[index].Cells[1].Value = capturing && phase == index && !result.Completed ?
            (result.Matches > 0 ? "已收到匹配事件" : "检测中，事件 " + result.Events) : result.Status(healthy);
        grid.Rows[index].Cells[2].Value = result.Detail;
        grid.Rows[index].DefaultCellStyle.BackColor = result.Matches > 0 ? Color.Honeydew : result.Completed ? Color.LemonChiffon : Color.White;
    }
    void Log(string text)
    {
        string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + text;
        if (trace.Length < 2000000) trace.AppendLine(line);
        if (events.TextLength > 70000) events.Text = events.Text.Substring(events.TextLength - 35000);
        events.AppendText(line + Environment.NewLine);
    }
    void WriteReport()
    {
        var text = new StringBuilder("T1Vibe 按键接收验证\r\n机器：" + Environment.MachineName + "\r\n桌面：" + desktop + "\r\n");
        text.AppendLine("注入事件保留；语音识别包含转发 F9。只按键码识别，不能仅凭键码确认物理来源。未见事件仅表示本次测试窗口内没有观测。\r\n");
        for (int i = 0; i < names.Length; i++) text.AppendLine(names[i] + ": " + results[i].Status(healthy) + "; 事件=" + results[i].Events + "; 匹配=" + results[i].Matches + "; " + results[i].Detail);
        text.AppendLine().Append(trace);
        try { File.WriteAllText(reportPath, text.ToString(), Encoding.UTF8); }
        catch (IOException) { summary.Text = "结果写入失败，请从窗口查看编码。"; }
        catch (UnauthorizedAccessException) { summary.Text = "结果目录不可写，请从窗口查看编码。"; }
    }

    void StartListening()
    {
        foreach (var device in devices.Values) device.Dispose(); devices.Clear();
        callback = OnKey; hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        Log("KEYBOARD_HOOK " + (hook == IntPtr.Zero ? "FAILED error=" + Marshal.GetLastWin32Error() : "OK (injected events included)"));
        shellMessage = RegisterWindowMessage("SHELLHOOK");
        Log("SHELL_HOOK " + (RegisterShellHookWindow(Handle) ? "OK" : "FAILED"));
        listening = true; RegisterDevices();
    }
    void StopListening()
    {
        listening = false;
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        DeregisterShellHookWindow(Handle);
        foreach (string value in registrations)
        {
            string[] parts = value.Split(':');
            RegisterRawInputDevices(new[] { new Registration { Page = ushort.Parse(parts[0]), Usage = ushort.Parse(parts[1]), Flags = 1 } }, 1, (uint)Marshal.SizeOf(typeof(Registration)));
        }
        registrations.Clear(); lock (pendingLock) pending.Clear();
    }
    void RegisterDevices()
    {
        var collections = new Dictionary<string, Registration>();
        AddCollection(collections, 1, 6); AddCollection(collections, 12, 1);
        uint count = 0, size = (uint)Marshal.SizeOf(typeof(DeviceEntry));
        if (GetRawInputDeviceList(null, ref count, size) != uint.MaxValue)
        {
            var list = new DeviceEntry[count];
            uint received = GetRawInputDeviceList(list, ref count, size);
            if (received == uint.MaxValue) { healthy = false; Log("DEVICE_ENUM FAILED error=" + Marshal.GetLastWin32Error()); }
            else for (int i = 0; i < received; i++)
            {
                var item = list[i]; string name = DeviceName(item.Handle);
                Log("DEVICE type=" + item.Type + " path=" + name);
                if (item.Type != 2) continue;
                uint length = 32; IntPtr info = Marshal.AllocHGlobal((int)length);
                try
                {
                    Marshal.WriteInt32(info, (int)length);
                    if (GetRawInputDeviceInfo(item.Handle, RidInfo, info, ref length) == uint.MaxValue) continue;
                    ushort page = (ushort)Marshal.ReadInt16(info, 20), usage = (ushort)Marshal.ReadInt16(info, 22);
                    Log("COLLECTION page=0x" + page.ToString("X4") + " usage=0x" + usage.ToString("X4"));
                    AddCollection(collections, page, usage);
                }
                finally { Marshal.FreeHGlobal(info); }
            }
        }
        else { healthy = false; Log("DEVICE_ENUM FAILED error=" + Marshal.GetLastWin32Error()); }
        foreach (var pair in collections)
        {
            if (registrations.Contains(pair.Key)) continue;
            if (RegisterRawInputDevices(new[] { pair.Value }, 1, (uint)Marshal.SizeOf(typeof(Registration)))) { registrations.Add(pair.Key); Log("RAW_REGISTER OK " + pair.Key); }
            else { healthy = false; Log("RAW_REGISTER FAILED " + pair.Key + " error=" + Marshal.GetLastWin32Error()); }
        }
    }
    void AddCollection(Dictionary<string, Registration> collections, ushort page, ushort usage)
    {
        if (page == 1 && usage == 2) return; // No mouse tracking.
        collections[page + ":" + usage] = new Registration { Page = page, Usage = usage, Flags = 0x2100, Target = Handle };
    }
    IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && capturing)
        {
            int msg = message.ToInt32();
            if (msg == 0x100 || msg == 0x104)
            {
                uint vk = (uint)Marshal.ReadInt32(data), scan = (uint)Marshal.ReadInt32(data, 4), flags = (uint)Marshal.ReadInt32(data, 8);
                var signal = new Signal { Key = KeyboardKey(vk), Detail = "vk=0x" + vk.ToString("X2") + " scan=0x" + scan.ToString("X2") + " injected=" + ((flags & 0x10) != 0) + " flags=0x" + flags.ToString("X2") };
                lock (pendingLock) { if (pending.Count < 4096) pending.Enqueue(signal); }
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }
    void Drain()
    {
        while (true) { Signal signal; lock (pendingLock) { if (pending.Count == 0) break; signal = pending.Dequeue(); } Record("KEYBOARD", signal.Detail, signal.Key); }
    }
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (!capturing) return base.ProcessCmdKey(ref message, keyData);
        Record("WINDOW", "key=" + keyData + " vk=0x" + ((int)(keyData & Keys.KeyCode)).ToString("X2"), KeyboardKey((uint)(keyData & Keys.KeyCode)));
        return true; // Keep test navigation keys from operating the form.
    }
    protected override void WndProc(ref Message message)
    {
        if (capturing && (message.Msg == 0x319 || (shellMessage != 0 && message.Msg == (int)shellMessage && message.WParam.ToInt32() == 12)))
        {
            int command = (int)((message.LParam.ToInt64() >> 16) & 0xfff);
            Record("APPCOMMAND", "code=" + command, CommandKey(command)); message.Result = new IntPtr(1); return;
        }
        if (listening && message.Msg == 0xfe)
        {
            Device device; if (devices.TryGetValue(message.LParam, out device)) { device.Dispose(); devices.Remove(message.LParam); }
            Log("DEVICE_CHANGE " + message.WParam); RegisterDevices();
        }
        if (capturing && message.Msg == 0xff) ReadRaw(message.LParam);
        base.WndProc(ref message);
    }
    void ReadRaw(IntPtr input)
    {
        uint size = 0, header = (uint)(8 + 2 * IntPtr.Size);
        if (GetRawInputData(input, RidInput, IntPtr.Zero, ref size, header) == uint.MaxValue || size < header + 8 || size > 1048576) return;
        IntPtr data = Marshal.AllocHGlobal((int)size);
        try
        {
            uint read = GetRawInputData(input, RidInput, data, ref size, header);
            if (read == uint.MaxValue || read < header + 8) return;
            IntPtr handle = Marshal.ReadIntPtr(data, 8); int type = Marshal.ReadInt32(data);
            if (type == 1 && read >= header + 16)
            {
                ushort flags = (ushort)Marshal.ReadInt16(data, (int)header + 2), vk = (ushort)Marshal.ReadInt16(data, (int)header + 6);
                if ((flags & 1) == 0) Record("RAW_KEYBOARD", "vk=0x" + vk.ToString("X2") + " device=" + DeviceName(handle), KeyboardKey(vk));
            }
            if (type != 2) return;
            int bytes = Marshal.ReadInt32(data, (int)header), count = Marshal.ReadInt32(data, (int)header + 4);
            if (bytes <= 0 || count <= 0 || (long)bytes * count > read - header - 8) return;
            Device device; if (!devices.TryGetValue(handle, out device)) { device = new Device(handle); devices.Add(handle, device); }
            for (int i = 0; i < count; i++)
            {
                byte[] report = new byte[bytes]; Marshal.Copy(IntPtr.Add(data, (int)header + 8 + i * bytes), report, 0, bytes);
                string decoded; int key = device.Decode(report, out decoded);
                Record("HID", "data=" + BitConverter.ToString(report, 0, Math.Min(bytes, 48)) + " " + decoded + " device=" + device.Name, key);
            }
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    static int KeyboardKey(uint vk)
    {
        switch (vk) { case 0x5d: return 0; case 0xac: return 1; case 0x26: return 2; case 0x28: return 3; case 0x0d: return 4; case 0xa6: case 0x1b: case 0x08: return 5; case 0x78: return 6; default: return -1; }
    }
    static int CommandKey(int command) { return command == 1 ? 5 : command == 7 ? 1 : command == 43 ? 6 : -1; }
    static int ConsumerKey(ushort page, ushort usage)
    {
        if (page != 12) return -1;
        switch (usage) { case 0xcf: return 6; case 0x223: return 1; case 0x224: return 5; case 0x40: return 0; case 0x41: return 4; case 0x42: return 2; case 0x43: return 3; default: return -1; }
    }
    static string DeviceName(IntPtr device)
    {
        uint length = 0; GetRawInputDeviceInfo(device, RidName, IntPtr.Zero, ref length);
        if (length == 0 || length > 32768) return "(unknown)";
        IntPtr data = Marshal.AllocHGlobal((int)(length + 1) * 2);
        try { return GetRawInputDeviceInfo(device, RidName, data, ref length) == uint.MaxValue ? "(unknown)" : Marshal.PtrToStringUni(data); }
        finally { Marshal.FreeHGlobal(data); }
    }
    static string DesktopName()
    {
        uint size; var buffer = new StringBuilder(512);
        return GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, buffer, 1024, out size) ? buffer.ToString() : "unknown";
    }
    sealed class Result
    {
        internal int Events, Matches; internal bool Completed, Interrupted; internal string Detail = "";
        internal string Status(bool healthy)
        {
            if (Matches > 0) return "已收到匹配事件";
            if (Interrupted) return "中止，不能判断";
            if (!Completed) return "待测";
            if (!healthy) return "监听不完整，不能判定未收到";
            return Events > 0 ? "有事件，未识别为此键" : "本项时段未见事件";
        }
    }
    sealed class Signal { internal int Key; internal string Detail; }
    sealed class Device : IDisposable
    {
        internal readonly string Name; IntPtr preparsed; uint capacity;
        internal Device(IntPtr handle)
        {
            Name = DeviceName(handle); uint size = 0; GetRawInputDeviceInfo(handle, RidPreparsed, IntPtr.Zero, ref size);
            if (size == 0 || size > 1048576) return;
            preparsed = Marshal.AllocHGlobal((int)size);
            if (GetRawInputDeviceInfo(handle, RidPreparsed, preparsed, ref size) == uint.MaxValue) { Dispose(); return; }
            capacity = HidP_MaxUsageListLength(0, 0, preparsed);
            if (capacity == 0 || capacity > 65536) Dispose();
        }
        internal int Decode(byte[] report, out string detail)
        {
            detail = "usages=(unavailable)";
            if (preparsed != IntPtr.Zero)
            {
                var usages = new Usage[capacity]; uint count = capacity;
                int status = HidP_GetUsagesEx(0, 0, usages, ref count, preparsed, report, (uint)report.Length);
                if (status == 0x110000 && count <= capacity)
                {
                    int key = -1; var text = new StringBuilder("usages=");
                    for (int i = 0; i < count; i++) { text.Append(usages[i].Page.ToString("X4")).Append(':').Append(usages[i].Id.ToString("X4")).Append(' '); int mapped = ConsumerKey(usages[i].Page, usages[i].Id); if (mapped >= 0) key = mapped; }
                    detail = text.ToString(); return key;
                }
                detail = "decode=0x" + status.ToString("X8");
            }
            if (report.Length == 3 && report[0] == 2)
            {
                ushort usage = (ushort)(report[1] | report[2] << 8); detail += " legacy=000C:" + usage.ToString("X4"); return ConsumerKey(12, usage);
            }
            return -1;
        }
        public void Dispose() { if (preparsed != IntPtr.Zero) { Marshal.FreeHGlobal(preparsed); preparsed = IntPtr.Zero; } }
    }
    static void SelfTest()
    {
        uint[] keys = { 0x5d, 0xac, 0x26, 0x28, 0x0d, 0xa6 };
        for (int i = 0; i < keys.Length; i++) if (KeyboardKey(keys[i]) != i) throw new Exception("Keyboard mapping " + i);
        if (KeyboardKey(0x41) != -1 || KeyboardKey(0x78) != 6 || ConsumerKey(1, 0xcf) != -1 || ConsumerKey(12, 0xcf) != 6 || CommandKey(43) != 6) throw new Exception("Voice classification");
        var result = new Result();
        if (result.Status(true) != "待测") throw new Exception("Untested must not mean absent");
        result.Completed = true; if (result.Status(true) != "本项时段未见事件") throw new Exception("No event");
        result.Events = 1; if (result.Status(true) != "有事件，未识别为此键") throw new Exception("Unknown event must not be called voice");
        if (result.Status(false) != "监听不完整，不能判定未收到") throw new Exception("Capture failure must not imply no input");
        result.Matches = 1; if (result.Status(true) != "已收到匹配事件") throw new Exception("Positive event");
        // Drive the real callback and queue with a remote-style injected event.
        using (var check = new KeyCheck())
        {
            check.capturing = true; check.phase = 0;
            IntPtr packet = Marshal.AllocHGlobal(32);
            try
            {
                for (int offset = 0; offset < 32; offset += 4) Marshal.WriteInt32(packet, offset, 0);
                Marshal.WriteInt32(packet, 0x5d); Marshal.WriteInt32(packet, 8, 0x10);
                check.OnKey(0, new IntPtr(0x100), packet); check.Drain();
                if (check.results[0].Matches != 1 || !check.results[0].Detail.Contains("injected=True")) throw new Exception("Remote injected key was lost");
                check.phase = 6; Marshal.WriteInt32(packet, 0x7c); // Unknown key during voice phase.
                check.OnKey(0, new IntPtr(0x100), packet); check.Drain();
                check.results[6].Completed = true;
                if (check.results[6].Events != 1 || check.results[6].Matches != 0 || check.results[6].Status(true) != "有事件，未识别为此键") throw new Exception("Unknown forwarded voice key was misclassified");
            }
            finally { Marshal.FreeHGlobal(packet); check.timer.Dispose(); }
        }
        Console.WriteLine("PASS: injected keyboard callback reaches results; unknown voice-phase input remains unclassified; incomplete capture never implies absence.");
    }
    [StructLayout(LayoutKind.Sequential)] struct Registration { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct DeviceEntry { public IntPtr Handle; public uint Type; }
    [StructLayout(LayoutKind.Sequential)] struct Usage { public ushort Id, Page; }
    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(Registration[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputDeviceList([Out] DeviceEntry[] devices, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string text);
    [DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool DeregisterShellHookWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder buffer, uint length, out uint needed);
    [DllImport("hid.dll")] static extern uint HidP_MaxUsageListLength(int type, ushort page, IntPtr preparsed);
    [DllImport("hid.dll")] static extern int HidP_GetUsagesEx(int type, ushort collection, [Out] Usage[] usages, ref uint count, IntPtr preparsed, byte[] report, uint length);
}
