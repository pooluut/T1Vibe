using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Windows.Automation;
using System.Windows.Forms;
class Bridge : Form
{
    internal enum RunMode { Local, VoiceRelay, UUReceiver, DirectVoiceExperiment }
    readonly RunMode mode;
    readonly NavigationKeys navigationKeys = new NavigationKeys();
    readonly NavigationWorker navigationWorker = new NavigationWorker();
    AutomationElement conversationFocus;
    static bool IsChatForeground() { return string.Equals(ForegroundProcessName(), "ChatGPT", StringComparison.OrdinalIgnoreCase); }
    static IntPtr FindForegroundChat() { return IsChatForeground() ? GetForegroundWindow() : IntPtr.Zero; }
    readonly RemoteVoiceKey remoteVoiceKey = new RemoteVoiceKey();
    readonly RelayPulse relayPulse = new RelayPulse();
    System.Windows.Forms.Timer relayTimer;
    Label relayStatus;
    int relayRecognized, relayForwarded, relaySkipped, relayReleaseAttempts;
    string relayMessage = "请先连接 UU，并将远程画面窗口置于前台。";
    static bool voiceDiagnostic;
    static readonly string voiceDiagnosticPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "T1Vibe-voice-hid.log");
    static void VoiceTrace(string text)
    {
        if (!voiceDiagnostic) return;
        try { System.IO.File.AppendAllText(voiceDiagnosticPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine); }
        catch (System.IO.IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    readonly Dictionary<IntPtr, ConsumerDevice> consumerDevices = new Dictionary<IntPtr, ConsumerDevice>();
    NotifyIcon tray; bool opening; IntPtr keyboardHook; HookProc hookProc; EventWaitHandle homeSignal; System.Windows.Forms.Timer homeTimer; uint shellMessage; uint nativeHomeMessage; IntPtr nativeModule, nativeHook; volatile bool conversationMode; IntPtr conversationWindow; AutomationElement conversationScroller; readonly object conversationLock = new object(); volatile bool sidebarMode; IntPtr sidebarWindow; int sidebarIndex = -1; int[] lastOpenedSidebarId; readonly object sidebarLock = new object();
    [STAThread] static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--check-package") { Environment.Exit(CheckPackage()); return; }
        if (args.Length > 0 && args[0] == "--home")
        {
            try { using (var signal = EventWaitHandle.OpenExisting("T1Vibe.Home")) signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }
        RunMode selected = RunMode.Local;
#if VOICE_RELAY
        selected = RunMode.VoiceRelay;
#elif UU_RECEIVER
        selected = RunMode.UUReceiver;
#endif
        if (Array.IndexOf(args, "--voice-direct-experiment") >= 0) selected = RunMode.DirectVoiceExperiment;
        else if (Array.IndexOf(args, "--voice-relay") >= 0) selected = RunMode.VoiceRelay;
        else if (Array.IndexOf(args, "--uu-receiver") >= 0) selected = RunMode.UUReceiver;
        bool created;
        using (var mutex = new Mutex(true, "T1Vibe.RemoteBridge", out created))
        {
            if (!created) { MessageBox.Show("T1Vibe 已在运行。请先从托盘退出旧程序，再启动所需模式。", "T1Vibe"); return; }
            voiceDiagnostic = Array.IndexOf(args, "--diagnose-voice") >= 0;
            if (voiceDiagnostic) System.IO.File.WriteAllText(voiceDiagnosticPath, "T1Vibe input trace; mode=" + selected + Environment.NewLine);
            Application.EnableVisualStyles(); Application.Run(new Bridge(selected));
        }
    }
    Bridge(RunMode selected)
    {
        mode = selected;
        Text = mode == RunMode.DirectVoiceExperiment ? "T1Vibe：直接 Win+H 实验" : mode == RunMode.VoiceRelay ? "T1Vibe：UU 语音转发端" : "T1 语音快捷键";
        var menu = new ContextMenuStrip();
        if (mode == RunMode.DirectVoiceExperiment)
        {
            menu.Items.Add("查看实验状态", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); });
            ClientSize = new System.Drawing.Size(640, 240);
            Font = new System.Drawing.Font("Microsoft YaHei UI", 10);
            var instructions = new Label { Dock = DockStyle.Top, Height = 140, Padding = new Padding(16), Text = "实验模式：语音键直接发送 Win+H，不发送 F9，不改变输入焦点。\r\n两端均可运行本模式。操作端将 UU 远程画面置于前台后按语音键。\r\n观察听写面板出现在本机还是远端；本机计数仅表示提交结果。\r\n本模式不接管返回、App、Home、方向或 OK 键，可最小化到托盘。" };
            relayStatus = new Label { Dock = DockStyle.Fill, Padding = new Padding(16), Text = "尚未发送 Win+H。" };
            Controls.Add(relayStatus); Controls.Add(instructions);
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
        }
        else if (mode == RunMode.VoiceRelay)
        {
            menu.Items.Add("查看转发状态", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); });
            ConfigureRelayWindow();
        }
        else
        {
            ShowInTaskbar = false; WindowState = FormWindowState.Minimized; Opacity = 0;
            menu.Items.Add(mode == RunMode.UUReceiver ? "UU 接收：ChatGPT 前台 F9 → Win + H" : "语音键 → Win + H").Enabled = false;
            menu.Items.Add("应用图标键 → 对话浏览；上下滚动").Enabled = false;
            menu.Items.Add("主页键 → 左侧导航；上下选择，确认打开").Enabled = false;
            menu.Items.Add("返回键 → ChatGPT 前台聚焦输入框").Enabled = false;
        }
        menu.Items.Add("退出", null, delegate { Close(); });
        tray = new NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = mode == RunMode.DirectVoiceExperiment ? "T1：直接 Win+H 实验" : mode == RunMode.VoiceRelay ? "T1：UU 语音转发端" : mode == RunMode.UUReceiver ? "T1：UU 语音接收 / F9 听写" : "T1：全局听写 / ChatGPT 导航", ContextMenuStrip = menu, Visible = true };
        if (mode == RunMode.VoiceRelay || mode == RunMode.DirectVoiceExperiment) tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
        Shown += delegate
        {
            if (mode != RunMode.VoiceRelay && mode != RunMode.DirectVoiceExperiment) { Hide(); StartAppHooks(); }
            RegisterConsumerInput();
        };
        FormClosed += delegate
        {
            navigationWorker.Dispose();
            if (relayTimer != null) { relayTimer.Stop(); relayPulse.Release(SendRelayKey); relayTimer.Dispose(); }
            if (nativeHook != IntPtr.Zero) UnhookWindowsHookEx(nativeHook);
            if (nativeModule != IntPtr.Zero) FreeLibrary(nativeModule);
            if (homeTimer != null) homeTimer.Dispose(); if (homeSignal != null) homeSignal.Dispose();
            DeregisterShellHookWindow(Handle); if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            foreach (var device in consumerDevices.Values) device.Dispose(); consumerDevices.Clear(); tray.Dispose();
        };
    }
    void StartAppHooks()
    {
        nativeHomeMessage = RegisterWindowMessage("T1Vibe.NativeHome"); nativeModule = LoadLibrary(ExtractHook());
        if (nativeModule != IntPtr.Zero)
        {
            IntPtr proc = GetProcAddress(nativeModule, "ShellProc"); if (proc != IntPtr.Zero) nativeHook = SetNativeHook(10, proc, nativeModule, 0);
        }
        if (nativeHook == IntPtr.Zero) MessageBox.Show("原生主页拦截模块未能加载，请退出后重试。");
        homeSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "T1Vibe.Home");
        homeTimer = new System.Windows.Forms.Timer { Interval = 100 };
        homeTimer.Tick += delegate { if (!IsChatForeground()) { conversationMode = false; sidebarMode = false; conversationFocus = null; } if (homeSignal.WaitOne(0)) HomePressed(); }; homeTimer.Start();
        shellMessage = RegisterWindowMessage("SHELLHOOK"); if (!RegisterShellHookWindow(Handle)) Notice("Shell 主页命令接管注册失败：" + Marshal.GetLastWin32Error());
        hookProc = OnKeyboard; keyboardHook = SetWindowsHookEx(13, hookProc, GetModuleHandle(null), 0);
        if (keyboardHook == IntPtr.Zero) { MessageBox.Show("按键拦截启动失败：" + Marshal.GetLastWin32Error()); Close(); }
    }
    void RegisterConsumerInput()
    {
        if (IsDisposed) return;
        var devices = new[] { new Registration { page = 12, usage = 1, flags = 0x2100, target = Handle } };
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf(typeof(Registration))))
        {
            int error = Marshal.GetLastWin32Error(); VoiceTrace("RAW registration failed: " + error); MessageBox.Show("按键监听启动失败：" + error); Close();
        }
        else VoiceTrace("RAW Consumer Control registered; mode=" + mode);
    }
    void ConfigureRelayWindow()
    {
        ClientSize = new System.Drawing.Size(640, 260); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        Font = new System.Drawing.Font("Microsoft YaHei UI", 10);
        var instructions = new Label { Dock = DockStyle.Top, Height = 100, Padding = new Padding(16), Text = "此程序运行在插着 T1 接收器的电脑上。\r\n另一台电脑运行 T1VibeUU.exe，并聚焦 ChatGPT 输入框。\r\n切回 UU 远程画面后按语音键；本窗口可最小化到托盘。" };
        relayStatus = new Label { Dock = DockStyle.Fill, Padding = new Padding(16) };
        Controls.Add(relayStatus); Controls.Add(instructions);
        Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
        relayTimer = new System.Windows.Forms.Timer { Interval = 80 };
        relayTimer.Tick += delegate
        {
            if (relayPulse.Release(SendRelayKey))
            {
                relayTimer.Stop(); relayForwarded++; relayMessage = "F9 已提交给 Windows；是否到达另一端请用检测程序确认。";
            }
            else if (++relayReleaseAttempts >= 5) { relayTimer.Stop(); relayMessage = "F9 松开事件发送失败，请退出转发端后重试。"; }
            UpdateRelayStatus();
        };
        UpdateRelayStatus();
    }
    void UpdateRelayStatus()
    {
        if (relayStatus == null) return;
        relayStatus.Text = "识别语音：" + relayRecognized + " 次    提交 F9：" + relayForwarded + " 次    跳过：" + relaySkipped + " 次\r\n\r\n" + relayMessage;
    }
    void ForwardVoice()
    {
        relayRecognized++;
        string foreground = ForegroundProcessName();
        if (!RelayForegroundAllowed(foreground))
        {
            relaySkipped++; relayMessage = "已收到语音键，当前前台为 " + foreground + "；请先切到 UU 远程画面。";
        }
        else if (relayPulse.TryStart(true, SendRelayKey))
        {
            relayReleaseAttempts = 0; relayTimer.Start(); relayMessage = "已提交 F9 按下，正在等待松开。";
        }
        else { relaySkipped++; relayMessage = "F9 发送失败或仍在等待松开，请检查两程序的运行权限。"; }
        VoiceTrace("RELAY foreground=" + foreground + " " + relayMessage); UpdateRelayStatus();
    }
    bool SendRelayKey(bool down)
    {
        // Use a standard F9 scan code; leave a measurable gap between down and up.
        var key = new INPUT { type = 1, u = new UNION { k = new KEY { scan = 0x43, flags = down ? 8U : 10U } } };
        uint sent = SendInput(1, new[] { key }, Marshal.SizeOf(typeof(INPUT)));
        VoiceTrace("RELAY F9 " + (down ? "down" : "up") + " sent=" + sent + " error=" + Marshal.GetLastWin32Error());
        return sent == 1;
    }
    static string ForegroundProcessName()
    {
        uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        try { using (var process = Process.GetProcessById((int)pid)) return process.ProcessName; }
        catch (ArgumentException) { return "(unknown)"; }
        catch (System.ComponentModel.Win32Exception) { return "(unknown)"; }
        catch (InvalidOperationException) { return "(unknown)"; }
    }
    internal static bool RelayForegroundAllowed(string process) { return string.Equals(process, "GameViewer", StringComparison.OrdinalIgnoreCase); }
    internal sealed class RelayPulse
    {
        internal bool Held { get; private set; }
        internal bool TryStart(bool allowed, Func<bool, bool> emit)
        {
            if (!allowed || Held || !emit(true)) return false;
            Held = true; return true;
        }
        internal bool Release(Func<bool, bool> emit)
        {
            if (!Held) return true;
            if (!emit(false)) return false;
            Held = false; return true;
        }
    }
    internal sealed class RemoteVoiceKey
    {
        bool held; IntPtr window;
        // Accept keyboard events irrespective of LLKHF_INJECTED: UU may inject them.
        internal bool Filter(uint vk, int message, IntPtr chat, out bool trigger)
        {
            trigger = false; if (vk != 0x78) return false;
            if (message == 0x100 || message == 0x104)
            {
                if (held) return true;
                if (chat == IntPtr.Zero) return false;
                held = true; window = chat; return true;
            }
            if (message == 0x101 || message == 0x105)
            {
                if (!held) return false;
                held = false; trigger = chat != IntPtr.Zero && chat == window; return true;
            }
            return false;
        }
    }
    // Keep this callback fast: defer all app activation and accessibility work.
    IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            if (mode == RunMode.UUReceiver && (uint)Marshal.ReadInt32(data) == 0x78)
            {
                bool voice; IntPtr chat = string.Equals(ForegroundProcessName(), "ChatGPT", StringComparison.OrdinalIgnoreCase) ? GetForegroundWindow() : IntPtr.Zero;
                if (remoteVoiceKey.Filter(0x78, message.ToInt32(), chat, out voice))
                {
                    if (voice)
                    {
                        bool injectedVoice = (Marshal.ReadInt32(data, 8) & 0x10) != 0;
                        BeginInvoke(new Action(delegate { if (GetForegroundWindow() != chat) return; VoiceTrace("UU F9 received; injected=" + injectedVoice); Dictate(); }));
                    }
                    return new IntPtr(1);
                }
            }
            uint vk = (uint)Marshal.ReadInt32(data); int msg = message.ToInt32();
            // Ignore only our own replay. UU input may carry LLKHF_INJECTED.
            if (Marshal.ReadIntPtr(data, 16) == new IntPtr(0x54315642))
                return CallNextHookEx(keyboardHook, code, message, data);
            bool chatForeground = IsChatForeground();
            if (!chatForeground) { conversationMode = false; sidebarMode = false; }
            bool dispatch;
            if (navigationKeys.Filter(vk, msg, chatForeground, out dispatch))
            {
                if (dispatch)
                {
                    IntPtr window = GetForegroundWindow();
                    BeginInvoke(new Action(delegate
                    {
                        if (GetForegroundWindow() != window || !IsChatForeground()) return;
                        if (vk == 0xa6) BackPressed();
                        else if (vk == 0xac) HomePressed();
                        else if (vk == 0x5d) ConversationPressed();
                        else navigationWorker.Post(delegate { NavigateActualFocus(window, vk); });
                    }));
                }
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(keyboardHook, code, message, data);
    }
    internal sealed class NavigationKeys
    {
        readonly HashSet<uint> held = new HashSet<uint>();
        internal bool Filter(uint key, int message, bool chat, out bool dispatch)
        {
            dispatch = false;
            if (message == 0x101 || message == 0x105) return held.Remove(key);
            if (message != 0x100 && message != 0x104) return false;
            bool claimed = held.Contains(key);
            bool supported = key == 0xa6 || key == 0xac || key == 0x5d || key == 0x26 || key == 0x28 || key == 0x0d;
            if (!claimed && !(chat && supported)) return false;
            bool first = held.Add(key);
            dispatch = chat && (first || key == 0x26 || key == 0x28);
            return true;
        }
    }
    // UI Automation can block while Chromium answers cross-process queries.
    // Keep it off the message loop that owns the low-level keyboard hook,
    // and preserve key order (including repeats) on one MTA worker.
    internal sealed class NavigationWorker : IDisposable
    {
        readonly BlockingCollection<Action> pending = new BlockingCollection<Action>();
        internal NavigationWorker()
        {
            var thread = new Thread(delegate()
            {
                foreach (var action in pending.GetConsumingEnumerable()) action();
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
        internal void Post(Action action) { pending.Add(action); }
        public void Dispose() { pending.CompleteAdding(); }
    }
    internal static bool FilterApps(uint vk, int message, ref bool down, out bool trigger)
    {
        trigger = false; if (vk != 0x5d) return false;
        if (message == 0x100 || message == 0x104) { down = true; return true; }
        if (message == 0x101 || message == 0x105) { trigger = down; down = false; return true; }
        return false;
    }
    static string ExtractHook()
    {
        using (var input = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("T1Vibe.HomeHook"))
        {
            if (input == null) throw new InvalidOperationException("内置按键模块缺失。");
            byte[] bytes = new byte[input.Length]; int offset = 0; while (offset < bytes.Length) { int n = input.Read(bytes, offset, bytes.Length - offset); if (n == 0) throw new System.IO.EndOfStreamException(); offset += n; }
            string hash; using (var sha = System.Security.Cryptography.SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
            string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "T1Vibe", hash); System.IO.Directory.CreateDirectory(folder);
            string path = System.IO.Path.Combine(folder, "HomeHook.dll");
            if (!System.IO.File.Exists(path)) System.IO.File.WriteAllBytes(path, bytes);
            byte[] disk = System.IO.File.ReadAllBytes(path); using (var sha = System.Security.Cryptography.SHA256.Create()) if (BitConverter.ToString(sha.ComputeHash(disk)).Replace("-", "") != hash) throw new InvalidOperationException("按键模块校验失败。");
            return path;
        }
    }
    static int CheckPackage() { IntPtr module = IntPtr.Zero, hook = IntPtr.Zero; try { module = LoadLibrary(ExtractHook()); if (module == IntPtr.Zero) return 2; var proc = GetProcAddress(module, "ShellProc"); if (proc == IntPtr.Zero) return 3; hook = SetNativeHook(10, proc, module, 0); return hook == IntPtr.Zero ? 4 : 0; } catch { return 5; } finally { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); if (module != IntPtr.Zero) FreeLibrary(module); } }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] static extern IntPtr SetNativeHook(int id, IntPtr proc, IntPtr module, uint thread);
    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    static string DeviceName(IntPtr device) { uint n = 0; GetRawInputDeviceInfo(device, 0x20000007, IntPtr.Zero, ref n); if (n == 0) return ""; IntPtr p = Marshal.AllocHGlobal((int)(n + 1) * 2); try { GetRawInputDeviceInfo(device, 0x20000007, p, ref n); return Marshal.PtrToStringUni(p) ?? ""; } finally { Marshal.FreeHGlobal(p); } }
    void Dictate()
    {
        string foreground = mode == RunMode.DirectVoiceExperiment ? ForegroundProcessName() : "";
        VoiceTrace("VOICE sending Win+H");
        var data = new INPUT[4]; data[0] = Key(0x5b, 0); data[1] = Key(0x48, 0); data[2] = Key(0x48, 2); data[3] = Key(0x5b, 2);
        uint sent = SendInput(4, data, Marshal.SizeOf(typeof(INPUT)));
        int error = Marshal.GetLastWin32Error();
        VoiceTrace("SendInput sent=" + sent + " error=" + error);
        if (mode == RunMode.DirectVoiceExperiment)
        {
            relayRecognized++;
            if (sent == 4) relayForwarded++;
            else SendInput(2, new[] { Key(0x48, 2), Key(0x5b, 2) }, Marshal.SizeOf(typeof(INPUT)));
            relayStatus.Text = "触发：" + relayRecognized + " 次    完整提交 Win+H：" + relayForwarded + " 次\r\n最近前台：" + foreground + "；提交事件：" + sent + "/4" + (sent == 4 ? "" : "；错误：" + error) + "\r\n此结果不能证明 UU 已将按键传到远端。";
        }
    }
    static INPUT Key(ushort code, uint flags) { return new INPUT { type = 1, u = new UNION { k = new KEY { vk = code, flags = flags, extra = new UIntPtr(0x54315642) } } }; }
    protected override void WndProc(ref Message m)
    {
        if (voiceDiagnostic && m.Msg == 0x319) VoiceTrace("APPCOMMAND code=" + ((m.LParam.ToInt64() >> 16) & 0x0fff));
        if (nativeHomeMessage != 0 && m.Msg == (int)nativeHomeMessage)
        {
            if (m.WParam.ToInt32() == 2) Dictate();
            else if (m.WParam.ToInt32() == 1) BackPressed();
            else HomePressed();
            return;
        }
        if ((m.Msg == 0x319) || (shellMessage != 0 && m.Msg == (int)shellMessage && m.WParam.ToInt32() == 12))
        {
            int command = (int)((m.LParam.ToInt64() >> 16) & 0x0fff);
            // The relay never starts local dictation, including when its status window has focus.
            if (mode == RunMode.VoiceRelay) { base.WndProc(ref m); return; }
            if (command == 43) { Dictate(); m.Result = new IntPtr(1); return; }
            if (command == 1 && IsChatForeground()) { BackPressed(); m.Result = new IntPtr(1); return; }
            if (command == 7 && IsChatForeground()) { HomePressed(); m.Result = new IntPtr(1); return; }
        }
        if (m.Msg == 0xfe) // WM_INPUT_DEVICE_CHANGE: discard stale descriptors and held keys.
        {
            ConsumerDevice device;
            if (consumerDevices.TryGetValue(m.LParam, out device)) { device.Dispose(); consumerDevices.Remove(m.LParam); }
        }
        if (m.Msg == 0xff) ReadConsumerInput(m.LParam);
        base.WndProc(ref m);
    }
    void ReadConsumerInput(IntPtr input)
    {
        uint size = 0, header = (uint)(8 + IntPtr.Size * 2);
        if (GetRawInputData(input, 0x10000003, IntPtr.Zero, ref size, header) == uint.MaxValue || size < header + 8 || size > 65536) return;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint read = GetRawInputData(input, 0x10000003, buffer, ref size, header);
            if (read == uint.MaxValue || read < header + 8 || Marshal.ReadInt32(buffer) != 2) return;
            IntPtr handle = Marshal.ReadIntPtr(buffer, 8);
            ConsumerDevice device;
            if (!consumerDevices.TryGetValue(handle, out device))
            {
                device = new ConsumerDevice(handle); consumerDevices.Add(handle, device);
                VoiceTrace("DEVICE " + DeviceName(handle) + " descriptor=" + (device.Preparsed != IntPtr.Zero));
            }
            int bytes = Marshal.ReadInt32(buffer, (int)header), count = Marshal.ReadInt32(buffer, (int)header + 4);
            if (!ValidReports(bytes, count, read - header - 8)) return;
            for (int i = 0; i < count; i++)
            {
                byte[] report = new byte[bytes]; Marshal.Copy(IntPtr.Add(buffer, (int)header + 8 + i * bytes), report, 0, bytes);
                if (voiceDiagnostic) VoiceTrace("RAW bytes=" + bytes + " data=" + BitConverter.ToString(report, 0, Math.Min(bytes, 32)));
                ushort[] usages;
                if (!device.TryUsages(report, out usages)) continue;
                bool voice = Array.IndexOf(usages, (ushort)0xcf) >= 0;
                if (device.Voice.Update(report[0], voice))
                {
                    if (mode == RunMode.VoiceRelay) { if (device.Target) ForwardVoice(); }
                    else Dictate();
                }
                if (mode == RunMode.VoiceRelay || mode == RunMode.DirectVoiceExperiment) continue;
                if (device.Target && Array.IndexOf(usages, (ushort)0x224) >= 0) BeginInvoke(new Action(BackPressed));
                else if (device.Target && Array.IndexOf(usages, (ushort)0x223) >= 0) BeginInvoke(new Action(HomePressed));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static bool ValidReports(int bytes, int count, uint available)
    {
        return bytes > 0 && count > 0 && (long)bytes * count <= available;
    }
    // A release from another report ID must not release the voice key.
    internal sealed class VoiceState
    {
        readonly HashSet<byte> held = new HashSet<byte>();
        internal bool Update(byte reportId, bool pressed)
        {
            if (!pressed) { held.Remove(reportId); return false; }
            return held.Add(reportId);
        }
    }
    internal static bool LegacyConsumerUsages(byte[] report, out ushort[] usages)
    {
        usages = null;
        // Retain the known T1 format only when Windows cannot decode its descriptor.
        if (report == null || report.Length != 3 || report[0] != 2) return false;
        usages = new[] { (ushort)(report[1] | (report[2] << 8)) }; return true;
    }
    sealed class ConsumerDevice : IDisposable
    {
        internal IntPtr Preparsed;
        internal readonly bool Target;
        internal readonly VoiceState Voice = new VoiceState();
        uint capacity;
        internal ConsumerDevice(IntPtr handle)
        {
            Target = DeviceName(handle).IndexOf("VID_1915&PID_1025", StringComparison.OrdinalIgnoreCase) >= 0;
            uint size = 0;
            GetRawInputDeviceInfo(handle, 0x20000005, IntPtr.Zero, ref size);
            if (size == 0 || size > 1048576) return;
            Preparsed = Marshal.AllocHGlobal((int)size);
            if (GetRawInputDeviceInfo(handle, 0x20000005, Preparsed, ref size) == uint.MaxValue) { Dispose(); return; }
            capacity = HidP_MaxUsageListLength(0, 0, Preparsed);
            if (capacity == 0 || capacity > 65536) Dispose();
        }
        internal bool TryUsages(byte[] report, out ushort[] usages)
        {
            if (Preparsed == IntPtr.Zero) return LegacyConsumerUsages(report, out usages);
            var active = new UsageAndPage[capacity]; uint count = capacity;
            int status = HidP_GetUsagesEx(0, 0, active, ref count, Preparsed, report, (uint)report.Length);
            usages = null;
            // Incompatible report IDs and malformed data must not clear held keys.
            if (status != 0x110000 || count > capacity) { VoiceTrace("HID decode status=0x" + status.ToString("X8")); return false; }
            var consumer = new List<ushort>();
            for (int i = 0; i < count; i++) if (active[i].Page == 12) consumer.Add(active[i].Usage);
            usages = consumer.ToArray(); return true;
        }
        public void Dispose() { if (Preparsed != IntPtr.Zero) { Marshal.FreeHGlobal(Preparsed); Preparsed = IntPtr.Zero; } }
    }
    [StructLayout(LayoutKind.Sequential)] struct UsageAndPage { public ushort Usage, Page; }
    [DllImport("hid.dll")] static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsed);
    [DllImport("hid.dll")] static extern int HidP_GetUsagesEx(int reportType, ushort linkCollection, [Out] UsageAndPage[] usages, ref uint count, IntPtr preparsed, byte[] report, uint reportLength);
    void ConversationPressed() { if (!IsChatForeground()) return; EnterConversation(); }
    void BackPressed() { if (!IsChatForeground()) return; OpenChat(); }
    void EnterConversation()
    {
        sidebarMode = false; conversationMode = false; navigationWorker.Post(delegate
        {
            lock (conversationLock)
            {
                try
                {
                    var window = FindForegroundChat(); if (window == IntPtr.Zero || GetForegroundWindow() != window) { Notice("请先切到 ChatGPT，再按应用图标键浏览对话。"); return; }
                    var root = AutomationElement.FromHandle(window);
                    AutomationElement best = FindConversationScroller(root);
                    if (best == null) { Notice("未找到可滚动的对话区域；请打开一段较长的对话后重试。"); return; }
                    AutomationElement focusTarget = best;
                    if (!best.Current.IsKeyboardFocusable) { focusTarget = null; var targets = best.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true)); foreach (AutomationElement target in targets) { if (!target.Current.IsOffscreen && !IsTextInput(target)) { focusTarget = target; break; } } }
                    if (GetForegroundWindow() != window) return;
                    if (focusTarget != null) focusTarget.SetFocus(); else root.SetFocus();
                    if (IsTextInput(AutomationElement.FocusedElement)) { Notice("未能将焦点移出输入框，对话浏览未启用。"); return; }
                    conversationFocus = AutomationElement.FocusedElement; conversationScroller = best; conversationWindow = window; conversationMode = true; Notice("对话浏览已启用：上下翻阅，返回键回到输入框。");
                }
                catch (Exception ex) { conversationMode = false; Notice("进入对话浏览失败：" + ex.Message); }
            }
        });
    }
    internal static AutomationElement FindConversationScroller(AutomationElement root)
    {
        var bounds = root.Current.BoundingRectangle;
        var candidates = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));
        AutomationElement best = null; double bestScore = 0;
        foreach (AutomationElement candidate in candidates)
        {
            try
            {
                var c = candidate.Current; var r = c.BoundingRectangle; object pattern;
                if (c.IsOffscreen || r.IsEmpty || r.Width < bounds.Width * 0.35 || r.Height < 100 || r.Left + r.Width / 2 < bounds.Left + bounds.Width * 0.4) continue;
                if (!candidate.TryGetCurrentPattern(ScrollPattern.Pattern, out pattern) || !((ScrollPattern)pattern).Current.VerticallyScrollable) continue;
                double score = r.Width * r.Height; if (score > bestScore) { best = candidate; bestScore = score; }
            }
            catch (ElementNotAvailableException) { }
        }
        return best;
    }
    void ScrollConversation(int direction)
    {
        {
            lock (conversationLock)
            {
                try
                {
                    if (!conversationMode || GetForegroundWindow() != conversationWindow) { conversationMode = false; return; }
                    object pattern; if (conversationScroller == null || !conversationScroller.TryGetCurrentPattern(ScrollPattern.Pattern, out pattern)) { conversationMode = false; Notice("对话区域已变化，请重新按应用图标键。"); return; }
                    var scroll = (ScrollPattern)pattern; if (scroll.Current.VerticallyScrollable) scroll.Scroll(ScrollAmount.NoAmount, direction < 0 ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement);
                }
                catch (Exception ex) { conversationMode = false; Notice("对话浏览停止：" + ex.Message); }
            }
        }
    }
    void HomePressed() { if (!IsChatForeground()) return; EnterSidebar(); }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterShellHookWindow(IntPtr handle);
    [DllImport("user32.dll")] static extern bool DeregisterShellHookWindow(IntPtr handle);
    void Notice(string message) { }
    void OpenChat()
    {
        conversationMode = false; sidebarMode = false; if (opening) return; opening = true; ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                IntPtr handle = FindForegroundChat(); if (handle == IntPtr.Zero) return;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    if (GetForegroundWindow() != handle) { Notice("聚焦已停止：当前窗口已切换。"); return; }
                    var root = AutomationElement.FromHandle(handle);
                    var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    AutomationElement best = null; int bestScore = -1; bool tie = false;
                    foreach (AutomationElement edit in edits)
                    {
                        var c = edit.Current; if (!c.IsEnabled || c.IsOffscreen || !c.IsKeyboardFocusable) continue; object value; if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out value) && ((ValuePattern)value).Current.IsReadOnly) continue;
                        string name = (c.Name + " " + c.AutomationId).ToLowerInvariant(); int score = 0;
                        if (name.Contains("search") || name.Contains("搜索") || name.Contains("查找")) continue;
                        if (name.Contains("message") || name.Contains("prompt") || name.Contains("消息") || name.Contains("询问") || name.Contains("输入")) score += 10;
                        if (score > bestScore) { best = edit; bestScore = score; tie = false; } else if (score == bestScore) tie = true;
                    }
                    if (best != null && !tie) { best.SetFocus(); Notice("已聚焦输入框，可以按语音键开始听写。"); return; }
                    Thread.Sleep(300);
                }
                Notice("已打开 ChatGPT，但未能确认输入框。请点击一次输入框，再按语音键。");
            }
            catch (Exception ex) { Notice("打开或聚焦失败：" + ex.Message); }
            finally { opening = false; }
        });
    }
    static bool SameRuntimeId(int[] a, int[] b) { if (a == null || b == null || a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    static bool FocusInside(AutomationElement row, AutomationElement focused) { for (int depth = 0; focused != null && depth < 12; depth++) { if (Automation.Compare(row, focused)) return true; if (focused.Current.ControlType == ControlType.Window) break; focused = TreeWalker.ControlViewWalker.GetParent(focused); } return false; }
    static bool IsSelectedRow(AutomationElement row) { if (SelectedState(row)) return true; var children = row.FindAll(TreeScope.Descendants, Condition.TrueCondition); foreach (AutomationElement child in children) if (SelectedState(child)) return true; return false; }
    static bool SelectedState(AutomationElement element)
    {
        object pattern; if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern) && ((SelectionItemPattern)pattern).Current.IsSelected) return true;
        var ariaProperty = AutomationProperty.LookupById(30102); if (ariaProperty == null) return false; string aria = element.GetCurrentPropertyValue(ariaProperty, true) as string;
        if (string.IsNullOrEmpty(aria)) return false;
        foreach (string part in aria.Split(';')) { string state = part.Trim().ToLowerInvariant().Replace(" ", ""); if (state == "selected=true" || state == "current=page" || state == "current=true" || state == "current=location") return true; }
        return false;
    }
    List<AutomationElement> SidebarItems(IntPtr window)
    {
        var root = AutomationElement.FromHandle(window); var rect = root.Current.BoundingRectangle;
        var types = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        var all = root.FindAll(TreeScope.Descendants, types); var result = new List<AutomationElement>();
        foreach (AutomationElement element in all)
        {
            try
            {
                var c = element.Current; var b = c.BoundingRectangle;
                if (c.IsOffscreen || !c.IsEnabled || !c.IsKeyboardFocusable || b.IsEmpty || b.Width < 65 || b.Height < 18) continue;
                // Keep only rows fully inside the left sidebar, excluding window chrome and footer controls.
                if (b.Left < rect.Left || b.Right > rect.Left + Math.Min(440, rect.Width * 0.42) || b.Top < rect.Top + 65 || b.Bottom > rect.Bottom - 55) continue;
                string name = (c.Name ?? "").Trim(); if (name.Length == 0) continue; string lower = name.ToLowerInvariant();
                if (lower.Contains("collapse") || lower.Contains("toggle sidebar") || lower.Contains("settings") || lower.Contains("设置") || lower.Contains("收起") || lower.Contains("more options") || lower.Contains("更多选项")) continue;
                bool duplicate = false; foreach (var previous in result) { var pb = previous.Current.BoundingRectangle; if (Math.Abs(pb.Top - b.Top) < 5) { duplicate = true; break; } }
                if (!duplicate) result.Add(element);
            }
            catch (ElementNotAvailableException) { }
        }
        result.Sort(delegate (AutomationElement a, AutomationElement b) { return a.Current.BoundingRectangle.Top.CompareTo(b.Current.BoundingRectangle.Top); }); return result;
    }
    void EnterSidebar()
    {
        conversationMode = false; navigationWorker.Post(delegate
        {
            lock (sidebarLock)
            {
                try
                {
                    IntPtr window = FindForegroundChat(); if (window == IntPtr.Zero) return;
                    var items = SidebarItems(window); if (items.Count == 0) { Notice("未找到左侧可聚焦的列表项。请展开侧边栏后重试。"); return; }
                    sidebarWindow = window; sidebarIndex = 0; var focused = AutomationElement.FocusedElement;
                    int chosen = -1; for (int i = 0; i < items.Count; i++) if (IsSelectedRow(items[i])) { chosen = i; break; }
                    if (chosen < 0) for (int i = 0; i < items.Count; i++) if (FocusInside(items[i], focused)) { chosen = i; break; }
                    if (chosen < 0 && lastOpenedSidebarId != null) for (int i = 0; i < items.Count; i++) if (SameRuntimeId(items[i].GetRuntimeId(), lastOpenedSidebarId)) { chosen = i; break; }
                    if (chosen >= 0) sidebarIndex = chosen;
                    items[sidebarIndex].SetFocus(); sidebarMode = true; Notice("左侧导航：" + items[sidebarIndex].Current.Name + "（上下选择，确认打开）");
                }
                catch (Exception ex) { sidebarMode = false; Notice("左侧导航失败：" + ex.Message); }
            }
        });
    }
    static bool IsTextInput(AutomationElement element)
    {
        // Editors can expose a nested text node rather than the edit control itself.
        for (int depth = 0; element != null && depth < 6; depth++)
        {
            var type = element.Current.ControlType;
            if (type == ControlType.Edit) return true;
            if (type == ControlType.Window) break;
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        return false;
    }
    void NavigateSidebar(int delta, bool activate, List<AutomationElement> items)
    {
        {
            lock (sidebarLock)
            {
                try
                {
                    if (!sidebarMode || GetForegroundWindow() != sidebarWindow) { sidebarMode = false; return; }
                    sidebarIndex = Math.Max(0, Math.Min(items.Count - 1, sidebarIndex + delta)); var item = items[sidebarIndex]; item.SetFocus(); if (activate) lastOpenedSidebarId = item.GetRuntimeId();
                    if (activate) { if (TryProjectRowNewChat(item, sidebarWindow)) { if (!projectCreated) return; sidebarMode = false; conversationMode = false; if (GetForegroundWindow() == sidebarWindow) BeginInvoke(new Action(OpenChat)); return; } object pattern; if (item.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) ((InvokePattern)pattern).Invoke(); else if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) ((SelectionItemPattern)pattern).Select(); else { Notice("此项不支持确认打开，请使用鼠标。"); return; } sidebarMode = false; Notice("已打开：" + item.Current.Name); }
                    else Notice("左侧选中：" + item.Current.Name);
                }
                catch (Exception ex) { sidebarMode = false; Notice("左侧导航停止：" + ex.Message); }
            }
        }
    }
    void NavigateActualFocus(IntPtr window, uint key)
    {
        try
        {
            if (GetForegroundWindow() != window) return;
            var focus = AutomationElement.FocusedElement;
            if (IsTextInput(focus)) { conversationMode = false; sidebarMode = false; ReplayKey(window, key); return; }
            // Repeated conversation keys need no full sidebar scan. Check actual
            // focus each time so a mouse focus change still takes effect at once.
            if (conversationMode && conversationWindow == window && conversationScroller != null &&
                ((conversationFocus != null && focus != null && Automation.Compare(conversationFocus, focus)) || FocusInside(conversationScroller, focus)))
            {
                if (key == 0x0d) OpenChat(); else ScrollConversation(key == 0x26 ? -1 : 1);
                return;
            }
            var items = SidebarItems(window);
            int row = -1;
            for (int i = 0; i < items.Count; i++) if (FocusInside(items[i], focus)) { row = i; break; }
            if (GetForegroundWindow() != window) return;
            if (row >= 0)
            {
                conversationMode = false; sidebarMode = true; sidebarWindow = window; sidebarIndex = row;
                NavigateSidebar(key == 0x26 ? -1 : key == 0x28 ? 1 : 0, key == 0x0d, items);
                return;
            }
            sidebarMode = false;
            bool browsing = false;
            if (!browsing && focus != null)
            {
                var root = AutomationElement.FromHandle(window); var bounds = root.Current.BoundingRectangle;
                for (var node = focus; node != null && !Automation.Compare(node, root); node = TreeWalker.ControlViewWalker.GetParent(node))
                {
                    object pattern; var rect = node.Current.BoundingRectangle;
                    if (!rect.IsEmpty && rect.Width >= bounds.Width * .35 && rect.Left + rect.Width / 2 >= bounds.Left + bounds.Width * .4 && node.TryGetCurrentPattern(ScrollPattern.Pattern, out pattern) && ((ScrollPattern)pattern).Current.VerticallyScrollable)
                    { conversationScroller = node; conversationWindow = window; browsing = true; break; }
                }
            }
            if (browsing)
            {
                conversationMode = true;
                if (key == 0x0d) OpenChat(); else ScrollConversation(key == 0x26 ? -1 : 1);
            }
            else { conversationMode = false; ReplayKey(window, key); }
        }
        catch (ElementNotAvailableException) { conversationMode = false; sidebarMode = false; }
        catch (InvalidOperationException) { conversationMode = false; sidebarMode = false; }
        catch (Exception ex) { conversationMode = false; sidebarMode = false; Notice("导航停止：" + ex.Message); }
    }
    void ReplayKey(IntPtr window, uint key)
    {
        if (GetForegroundWindow() == window) SendInput(2, new[] { Key((ushort)key, 0), Key((ushort)key, 2) }, Marshal.SizeOf(typeof(INPUT)));
    }
    bool projectCreated;
    bool TryProjectRowNewChat(AutomationElement item, IntPtr window)
    {
        projectCreated = false;
        var original = item.Current.BoundingRectangle;
        bool project = false; AutomationElement ancestor = item;
        for (int depth = 0; ancestor != null && depth < 5; depth++)
        {
            var r = ancestor.Current.BoundingRectangle; if (r.IsEmpty || r.Height > Math.Max(80, original.Height * 2)) break;
            object expand; if (ancestor.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out expand)) project = true;
            ancestor = TreeWalker.ControlViewWalker.GetParent(ancestor);
        }
        var root = AutomationElement.FromHandle(window); var bounds = root.Current.BoundingRectangle;
        var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        AutomationElement create = null; bool ambiguous = false;
        foreach (AutomationElement candidate in all)
        {
            try
            {
                var c = candidate.Current; var r = c.BoundingRectangle;
                if (r.IsEmpty || r.Height > Math.Max(80, original.Height * 2) || r.Left < original.Left - 8 || r.Right > bounds.Left + Math.Min(600, bounds.Width * .48)) continue;
                if (Math.Abs((r.Top + r.Height / 2) - (original.Top + original.Height / 2)) > Math.Max(12, original.Height * .55)) continue;
                string label = ((c.Name ?? "") + " " + (c.HelpText ?? "") + " " + (c.AutomationId ?? "")).Trim().ToLowerInvariant();
                object inv; bool invokable = candidate.TryGetCurrentPattern(InvokePattern.Pattern, out inv);
                bool named = IsNewConversationLabel(c.Name);
                if (!named || !c.IsEnabled || !invokable) continue;
                if (create != null && !Automation.Compare(create, candidate)) ambiguous = true; else create = candidate;
            }
            catch (ElementNotAvailableException) { }
        }
        if (create != null && !ambiguous) { if (GetForegroundWindow() != window) return true; object invoke; create.TryGetCurrentPattern(InvokePattern.Pattern, out invoke); ((InvokePattern)invoke).Invoke(); projectCreated = true; return true; }
        if (project || ambiguous) { Notice(ambiguous ? "同行存在多个新建按钮，尚未执行。" : "项目行的新建对话按钮尚未识别。"); return true; }
        return false;
    }
    internal static bool IsNewConversationLabel(string name)
    {
        string label = (name ?? "").Trim().ToLowerInvariant();
        return (label.StartsWith("在 ") && label.EndsWith(" 中开始新聊天")) || label.StartsWith("start new chat in ") || label == "新建对话" || label == "新对话" || label == "新建聊天" || label == "新聊天" || label == "new chat" || label == "new conversation" || label == "new thread";
    }
    void PassEnter(IntPtr window) { if (GetForegroundWindow() != window) return; SendInput(2, new[] { Key(0x0d, 0), Key(0x0d, 2) }, Marshal.SizeOf(typeof(INPUT))); }
    void ProjectEnter(IntPtr window)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                if (GetForegroundWindow() != window) return;
                var focus = AutomationElement.FocusedElement;
                if (!IsTextInput(focus) && focus != null && TryProjectRowNewChat(focus, window))
                {
                    if (!projectCreated) return;
                    sidebarMode = false; conversationMode = false;
                    if (GetForegroundWindow() == window) BeginInvoke(new Action(OpenChat)); return;
                }
                PassEnter(window);
            }
            catch (Exception) { }
        });
    }
    static void ActivateChat(IntPtr handle)
    {
        Thread.Sleep(80);
        IntPtr foreground = GetForegroundWindow(); uint pid; uint foregroundThread = GetWindowThreadProcessId(foreground, out pid); uint ownThread = GetCurrentThreadId(); bool attached = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != ownThread) attached = AttachThreadInput(ownThread, foregroundThread, true);
            if (IsIconic(handle)) ShowWindowAsync(handle, 9);
            BringWindowToTop(handle); SetForegroundWindow(handle);
        }
        finally { if (attached) AttachThreadInput(ownThread, foregroundThread, false); }
    }
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint source, uint target, bool attach);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    static IntPtr FindChat() { foreach (var process in Process.GetProcessesByName("ChatGPT")) { using (process) { try { if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle; } catch { } } } return IntPtr.Zero; }


    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr h, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] struct Registration { public ushort page, usage; public uint flags; public IntPtr target; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public UNION u; }
    [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public KEY k; [FieldOffset(0)] public MOUSE m; }
    [StructLayout(LayoutKind.Sequential)] struct KEY { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSE { public int x, y; public uint data, flags, time; public UIntPtr extra; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(Registration[] d, uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr device, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr raw, uint cmd, IntPtr data, ref uint size, uint header);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
