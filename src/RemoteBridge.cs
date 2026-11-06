using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Windows.Automation;
using System.Windows.Forms;
class Bridge : Form
{
    NotifyIcon tray; readonly HashSet<IntPtr> voiceHeld = new HashSet<IntPtr>(); DateTime last = DateTime.MinValue; bool opening; IntPtr keyboardHook; HookProc hookProc; bool enterDown; bool appsDown; bool homeDown; EventWaitHandle homeSignal; System.Windows.Forms.Timer homeTimer; uint shellMessage; uint nativeHomeMessage; IntPtr nativeModule, nativeHook; DateTime lastHome = DateTime.MinValue; volatile bool conversationMode; IntPtr conversationWindow; AutomationElement conversationScroller; bool backDown; DateTime lastBack = DateTime.MinValue; readonly object conversationLock = new object(); volatile bool sidebarMode; IntPtr sidebarWindow; int sidebarIndex = -1; int[] lastOpenedSidebarId; readonly object sidebarLock = new object();
    [STAThread] static void Main(string[] args) { if (args.Length > 0 && args[0] == "--check-package") { Environment.Exit(CheckPackage()); return; } if (args.Length > 0 && args[0] == "--home") { try { using (var signal = EventWaitHandle.OpenExisting("T1Vibe.Home")) { signal.Set(); } } catch (WaitHandleCannotBeOpenedException) { } return; } bool created; using (var mutex = new Mutex(true, "T1Vibe.RemoteBridge", out created)) { if (!created) return; Application.EnableVisualStyles(); Application.Run(new Bridge()); } }
    Bridge() { Text = "T1 语音快捷键"; ShowInTaskbar = false; WindowState = FormWindowState.Minimized; Opacity = 0; var menu = new ContextMenuStrip(); menu.Items.Add("语音键 → Win + H").Enabled = false; menu.Items.Add("应用图标键 → 打开 ChatGPT 并聚焦输入框").Enabled = false; menu.Items.Add("主页键 → 左侧导航；上下选择，确认打开").Enabled = false; menu.Items.Add("返回键 → 对话浏览；上下滚动").Enabled = false; menu.Items.Add("退出", null, delegate { Close(); }); tray = new NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "T1：语音听写 / 应用键打开 ChatGPT", ContextMenuStrip = menu, Visible = true }; Shown += delegate { Hide(); nativeHomeMessage = RegisterWindowMessage("T1Vibe.NativeHome"); nativeModule = LoadLibrary(ExtractHook()); if (nativeModule != IntPtr.Zero) { IntPtr proc = GetProcAddress(nativeModule, "ShellProc"); if (proc != IntPtr.Zero) nativeHook = SetNativeHook(10, proc, nativeModule, 0); } if (nativeHook == IntPtr.Zero) { MessageBox.Show("原生主页拦截模块未能加载，请退出后重试。"); } homeSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "T1Vibe.Home"); homeTimer = new System.Windows.Forms.Timer { Interval = 100 }; homeTimer.Tick += delegate { if (homeSignal.WaitOne(0)) HomePressed(); }; homeTimer.Start(); shellMessage = RegisterWindowMessage("SHELLHOOK"); if (!RegisterShellHookWindow(Handle)) Notice("Shell 主页命令接管注册失败：" + Marshal.GetLastWin32Error()); hookProc = OnKeyboard; keyboardHook = SetWindowsHookEx(13, hookProc, GetModuleHandle(null), 0); if (keyboardHook == IntPtr.Zero) { MessageBox.Show("菜单键拦截启动失败：" + Marshal.GetLastWin32Error()); Close(); return; } var devices = new[] { new Registration { page = 12, usage = 1, flags = 0x100, target = Handle } }; if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf(typeof(Registration)))) { MessageBox.Show("按键监听启动失败：" + Marshal.GetLastWin32Error()); Close(); } }; FormClosed += delegate { if (nativeHook != IntPtr.Zero) UnhookWindowsHookEx(nativeHook); if (nativeModule != IntPtr.Zero) FreeLibrary(nativeModule); if (homeTimer != null) homeTimer.Dispose(); if (homeSignal != null) homeSignal.Dispose(); DeregisterShellHookWindow(Handle); if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook); tray.Dispose(); }; }
    // Keep this callback fast: defer all app activation and accessibility work.
    IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            bool trigger; uint vk = (uint)Marshal.ReadInt32(data); int msg = message.ToInt32(); bool injected = (Marshal.ReadInt32(data, 8) & 0x10) != 0; if (!injected && vk == 0xa6) { if (msg == 0x100 || msg == 0x104) backDown = true; else if (msg == 0x101 || msg == 0x105) { if (backDown) { backDown = false; BeginInvoke(new Action(BackPressed)); } } return new IntPtr(1); }
            if (!injected && conversationMode && (vk == 0x26 || vk == 0x28)) { if (GetForegroundWindow() == conversationWindow) { if (msg == 0x100) { int direction = vk == 0x26 ? -1 : 1; BeginInvoke(new Action(delegate { ScrollConversation(direction); })); } return new IntPtr(1); } conversationMode = false; }
            if (!injected && vk == 0xac) { if (msg == 0x100 || msg == 0x104) homeDown = true; else if (msg == 0x101 || msg == 0x105) { if (homeDown) { homeDown = false; BeginInvoke(new Action(HomePressed)); } } return new IntPtr(1); }
            if (!injected && vk == 0x0d && !sidebarMode) { if (msg == 0x101 || msg == 0x105) { if (enterDown) { enterDown = false; return new IntPtr(1); } } else if (msg == 0x100 || msg == 0x104) { IntPtr chat = FindChat(); if (chat != IntPtr.Zero && GetForegroundWindow() == chat) { if (!enterDown) { enterDown = true; BeginInvoke(new Action(delegate { ProjectEnter(chat); })); } return new IntPtr(1); } } }
            if (!injected && sidebarMode && (vk == 0x26 || vk == 0x28 || vk == 0x0d)) { if (GetForegroundWindow() == sidebarWindow) { if (msg == 0x100) { int move = vk == 0x26 ? -1 : vk == 0x28 ? 1 : 0; BeginInvoke(new Action(delegate { NavigateSidebar(move, move == 0); })); } return new IntPtr(1); } sidebarMode = false; }
            if (FilterApps(vk, message.ToInt32(), ref appsDown, out trigger))
            {
                if (trigger && !IsDisposed) BeginInvoke(new Action(OpenChat));
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(keyboardHook, code, message, data);
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
    void Dictate() { var data = new INPUT[4]; data[0] = Key(0x5b, 0); data[1] = Key(0x48, 0); data[2] = Key(0x48, 2); data[3] = Key(0x5b, 2); uint sent = SendInput(4, data, Marshal.SizeOf(typeof(INPUT))); if (sent != 4) { tray.BalloonTipTitle = "听写快捷键发送失败"; tray.BalloonTipText = "请在普通权限的输入框中重试。"; tray.ShowBalloonTip(3000); } }
    static INPUT Key(ushort code, uint flags) { return new INPUT { type = 1, u = new UNION { k = new KEY { vk = code, flags = flags } } }; }
    protected override void WndProc(ref Message m) { if (nativeHomeMessage != 0 && m.Msg == (int)nativeHomeMessage) { if (m.WParam.ToInt32() == 1) { BackPressed(); } else { HomePressed(); } return; } if (shellMessage != 0 && m.Msg == (int)shellMessage && m.WParam.ToInt32() == 12) { int command = (int)((m.LParam.ToInt64() >> 16) & 0x0fff); if (command == 1) { BackPressed(); m.Result = new IntPtr(1); return; } if (command == 7) { HomePressed(); m.Result = new IntPtr(1); return; } } if (m.Msg == 0xff) { uint n = 0, hs = (uint)(8 + IntPtr.Size * 2); GetRawInputData(m.LParam, 0x10000003, IntPtr.Zero, ref n, hs); if (n >= hs + 8) { IntPtr p = Marshal.AllocHGlobal((int)n); try { if (GetRawInputData(m.LParam, 0x10000003, p, ref n, hs) != uint.MaxValue) { IntPtr device = Marshal.ReadIntPtr(p, 8); bool target = DeviceName(device).IndexOf("VID_1915&PID_1025", StringComparison.OrdinalIgnoreCase) >= 0; int o = (int)hs; if (Marshal.ReadInt32(p) == 2) { int bytes = Marshal.ReadInt32(p, o), count = Marshal.ReadInt32(p, o + 4); if (bytes >= 3 && count >= 0 && (long)bytes * count <= n - hs - 8) { for (int i = 0; i < count; i++) { int pos = o + 8 + i * bytes; if (Marshal.ReadByte(p, pos) != 2) continue; int usage = (ushort)Marshal.ReadInt16(p, pos + 1); if (usage == 0) { voiceHeld.Remove(device); } else if (target && usage == 0x224) { BeginInvoke(new Action(BackPressed)); } else if (target && usage == 0x223) { BeginInvoke(new Action(HomePressed)); } else if (usage == 0xcf) { if (!voiceHeld.Contains(device) && (DateTime.UtcNow - last).TotalMilliseconds > 500) { last = DateTime.UtcNow; Dictate(); } voiceHeld.Add(device); } } } } } } finally { Marshal.FreeHGlobal(p); } } } base.WndProc(ref m); }
    void BackPressed() { if ((DateTime.UtcNow - lastBack).TotalMilliseconds < 700) return; lastBack = DateTime.UtcNow; EnterConversation(); }
    void EnterConversation()
    {
        sidebarMode = false; conversationMode = false; ThreadPool.QueueUserWorkItem(delegate
        {
            lock (conversationLock)
            {
                try
                {
                    var window = FindChat(); if (window == IntPtr.Zero || GetForegroundWindow() != window) { Notice("请先切到 ChatGPT，再按返回键浏览对话。"); return; }
                    var root = AutomationElement.FromHandle(window); var bounds = root.Current.BoundingRectangle;
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
                    if (best == null) { Notice("未找到可滚动的对话区域；请打开一段较长的对话后重试。"); return; }
                    AutomationElement focusTarget = best;
                    if (!best.Current.IsKeyboardFocusable) { focusTarget = null; var targets = best.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true)); foreach (AutomationElement target in targets) { if (!target.Current.IsOffscreen && !IsTextInput(target)) { focusTarget = target; break; } } }
                    if (GetForegroundWindow() != window) return;
                    if (focusTarget != null) focusTarget.SetFocus(); else root.SetFocus();
                    if (IsTextInput(AutomationElement.FocusedElement)) { Notice("未能将焦点移出输入框，对话浏览未启用。"); return; }
                    conversationScroller = best; conversationWindow = window; conversationMode = true; Notice("对话浏览已启用：上下翻阅，应用图标键回到输入框。");
                }
                catch (Exception ex) { conversationMode = false; Notice("进入对话浏览失败：" + ex.Message); }
            }
        });
    }
    void ScrollConversation(int direction)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            lock (conversationLock)
            {
                try
                {
                    if (!conversationMode || GetForegroundWindow() != conversationWindow) { conversationMode = false; return; }
                    if (IsTextInput(AutomationElement.FocusedElement)) { conversationMode = false; ushort key = direction < 0 ? (ushort)0x26 : (ushort)0x28; SendInput(2, new[] { Key(key, 0), Key(key, 2) }, Marshal.SizeOf(typeof(INPUT))); return; }
                    object pattern; if (conversationScroller == null || !conversationScroller.TryGetCurrentPattern(ScrollPattern.Pattern, out pattern)) { conversationMode = false; Notice("对话区域已变化，请重新按返回键。"); return; }
                    var scroll = (ScrollPattern)pattern; if (scroll.Current.VerticallyScrollable) scroll.Scroll(ScrollAmount.NoAmount, direction < 0 ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement);
                }
                catch (Exception ex) { conversationMode = false; Notice("对话浏览停止：" + ex.Message); }
            }
        });
    }
    void HomePressed() { if ((DateTime.UtcNow - lastHome).TotalMilliseconds < 700) return; lastHome = DateTime.UtcNow; EnterSidebar(); }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterShellHookWindow(IntPtr handle);
    [DllImport("user32.dll")] static extern bool DeregisterShellHookWindow(IntPtr handle);
    void Notice(string message) { if (IsDisposed) return; BeginInvoke(new Action(delegate { tray.BalloonTipTitle = "T1 遥控器"; tray.BalloonTipText = message; tray.ShowBalloonTip(3500); })); }
    void OpenChat()
    {
        conversationMode = false; sidebarMode = false; if (opening) return; opening = true; ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                IntPtr handle = FindChat(); if (handle == IntPtr.Zero) { Process.Start("explorer.exe", "shell:AppsFolder\\OpenAI.Codex_2p2nqsd0c76g0!App"); for (int i = 0; i < 40 && handle == IntPtr.Zero; i++) { Thread.Sleep(250); handle = FindChat(); } }
                if (handle == IntPtr.Zero) { Notice("未找到 ChatGPT 窗口，请先手动打开应用。"); return; }
                ActivateChat(handle); Thread.Sleep(300);
                // Verify the target owns the foreground before looking for its input field.
                if (GetForegroundWindow() != handle) { Notice("窗口未能切到前台，请先点击 ChatGPT 后重试。"); return; }

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
                    if (best != null && !tie) { best.SetFocus(); Thread.Sleep(100); var focused = AutomationElement.FocusedElement; if (focused != null && Automation.Compare(best, focused)) { Notice("已聚焦输入框，可以按语音键开始听写。"); return; } }
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
        conversationMode = false; ThreadPool.QueueUserWorkItem(delegate
        {
            lock (sidebarLock)
            {
                try
                {
                    IntPtr window = FindChat(); if (window == IntPtr.Zero) { Notice("请先用应用图标键打开 ChatGPT，再按主页键。"); return; }
                    ActivateChat(window); Thread.Sleep(200); if (GetForegroundWindow() != window) { Notice("未能切到 ChatGPT，左侧导航未启用。"); return; }
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
    void NavigateSidebar(int delta, bool activate)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            lock (sidebarLock)
            {
                try
                {
                    if (!sidebarMode || GetForegroundWindow() != sidebarWindow) { sidebarMode = false; return; }
                    var activeFocus = AutomationElement.FocusedElement; if (IsTextInput(activeFocus)) { sidebarMode = false; ushort key = activate ? (ushort)0x0d : delta < 0 ? (ushort)0x26 : (ushort)0x28; SendInput(2, new[] { Key(key, 0), Key(key, 2) }, Marshal.SizeOf(typeof(INPUT))); return; }
                    var items = SidebarItems(sidebarWindow); if (items.Count == 0) { sidebarMode = false; Notice("左侧列表已变化，请重新按主页键。"); return; }
                    var focused = AutomationElement.FocusedElement; for (int i = 0; i < items.Count; i++) if (focused != null && Automation.Compare(focused, items[i])) { sidebarIndex = i; break; }
                    sidebarIndex = Math.Max(0, Math.Min(items.Count - 1, sidebarIndex + delta)); var item = items[sidebarIndex]; item.SetFocus(); if (activate) lastOpenedSidebarId = item.GetRuntimeId();
                    if (activate) { if (TryProjectRowNewChat(item, sidebarWindow)) { if (!projectCreated) return; sidebarMode = false; conversationMode = false; Thread.Sleep(350); if (GetForegroundWindow() == sidebarWindow) BeginInvoke(new Action(OpenChat)); return; } object pattern; if (item.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) ((InvokePattern)pattern).Invoke(); else if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) ((SelectionItemPattern)pattern).Select(); else { Notice("此项不支持确认打开，请使用鼠标。"); return; } sidebarMode = false; Notice("已打开：" + item.Current.Name); }
                    else Notice("左侧选中：" + item.Current.Name);
                }
                catch (Exception ex) { sidebarMode = false; Notice("左侧导航停止：" + ex.Message); }
            }
        });
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
                    sidebarMode = false; conversationMode = false; Thread.Sleep(350);
                    if (GetForegroundWindow() == window) BeginInvoke(new Action(OpenChat)); return;
                }
                PassEnter(window);
            }
            catch (Exception) { }
        });
    }
    static void ActivateChat(IntPtr handle)
    {
        // Apps down and up are both consumed by the low-level keyboard hook.

        Thread.Sleep(80);
        IntPtr foreground = GetForegroundWindow(); uint pid; uint foregroundThread = GetWindowThreadProcessId(foreground, out pid); uint ownThread = GetCurrentThreadId(); bool attached = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != ownThread) attached = AttachThreadInput(ownThread, foregroundThread, true);
            ShowWindowAsync(handle, 9); BringWindowToTop(handle); SetForegroundWindow(handle);
        }
        finally { if (attached) AttachThreadInput(ownThread, foregroundThread, false); }
    }
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint source, uint target, bool attach);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    static IntPtr FindChat() { foreach (var process in Process.GetProcessesByName("ChatGPT")) { using (process) { try { if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle; } catch { } } } return IntPtr.Zero; }


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
