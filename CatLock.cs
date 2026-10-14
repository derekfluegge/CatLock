// CatLock - press Ctrl+Esc to lock keyboard & mouse, press it again to unlock.
// Build: build.cmd   (uses the C# compiler that ships with Windows / .NET Framework 4)
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "CatLock_SingleInstance", out created))
        {
            if (!created) return; // already running
            Application.EnableVisualStyles();
            Application.Run(new CatLockContext());
        }
    }
}

class CatLockContext : ApplicationContext
{
    // ---- The combo: Ctrl + this key ----
    const Keys ComboKey = Keys.Escape;
    const string ComboText = "Ctrl+Esc";

    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, WM_QUIT = 0x12;
    const uint LLKHF_INJECTED = 0x10;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    // Keep delegates referenced so the GC doesn't collect them while hooked.
    readonly HookProc kbProc, mouseProc;
    IntPtr kbHook, mouseHook;
    uint hookThreadId;

    readonly bool[] down = new bool[256]; // physical key state, tracked from the hook itself
    volatile bool locked;

    readonly NotifyIcon tray;
    readonly OverlayForm overlay;
    readonly ShieldForm shield = new ShieldForm();
    readonly CatWalker cat = new CatWalker();
    readonly Icon unlockedIcon = CatWalker.MakeIcon(false), lockedIcon = CatWalker.MakeIcon(true);

    public CatLockContext()
    {
        overlay = new OverlayForm("\U0001F512  Locked — press " + ComboText + " to unlock");
        var _ = overlay.Handle; // create handle so BeginInvoke works

        var menu = new ContextMenuStrip();
        menu.Items.Add("Lock now", null, (s, e) => SetLocked(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => Exit());

        tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        UpdateTray();

        kbProc = KeyboardHook;
        mouseProc = MouseHook;
        if (!StartHookThread())
        {
            MessageBox.Show("Failed to install input hooks.", "CatLock", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Exit();
            return;
        }

        tray.ShowBalloonTip(3000, "CatLock is running", "Press " + ComboText + " to lock / unlock.", ToolTipIcon.Info);
    }

    // The hooks live on their own high-priority thread with its own message loop, so the cat
    // animation (or anything else on the UI thread) can never delay them. If a low-level hook
    // is slow to answer, Windows gives up on it and lets the input through.
    bool StartHookThread()
    {
        bool ok = false;
        using (var ready = new ManualResetEvent(false))
        {
            var t = new Thread(() =>
            {
                hookThreadId = GetCurrentThreadId();
                IntPtr mod = GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName);
                kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, kbProc, mod, 0);
                mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, mod, 0);
                ok = kbHook != IntPtr.Zero && mouseHook != IntPtr.Zero;
                ready.Set();
                if (!ok) return;

                MSG msg;
                while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { } // hooks are called from inside GetMessage

                UnhookWindowsHookEx(kbHook);
                UnhookWindowsHookEx(mouseHook);
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.Highest;
            t.Start();
            ready.WaitOne();
        }
        return ok;
    }

    bool Ctrl() { return down[0xA2] || down[0xA3]; }
    static bool IsModifier(uint vk) { return vk >= 0xA0 && vk <= 0xA5 || vk == 0x5B || vk == 0x5C; }

    IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            int msg = wParam.ToInt32();
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            bool injected = (k.flags & LLKHF_INJECTED) != 0;
            uint vk = k.vkCode & 0xFF;

            bool isRepeat = isDown && down[vk];
            if (!injected)
            {
                if (isDown) down[vk] = true;
                else if (isUp) down[vk] = false;
            }

            if (!injected && vk == (uint)ComboKey && Ctrl())
            {
                if (isDown && !isRepeat) SetLocked(!locked);
                return (IntPtr)1; // never let the combo key itself through
            }

            if (locked)
            {
                // Let modifier releases through so Ctrl/Alt/Shift/Win don't get "stuck" down.
                if (isUp && IsModifier(vk)) return CallNextHookEx(kbHook, nCode, wParam, lParam);
                return (IntPtr)1; // swallow everything else (including keys injected by touchpad drivers)
            }
        }
        return CallNextHookEx(kbHook, nCode, wParam, lParam);
    }

    IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && locked) return (IntPtr)1;
        return CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    void SetLocked(bool value)
    {
        locked = value;
        // Update UI asynchronously so the hook callback returns immediately.
        overlay.BeginInvoke((Action)(() =>
        {
            if (locked)
            {
                shield.Cover();          // first, so the banner and cat end up above it
                overlay.ShowOverlay();
                cat.Start();
            }
            else
            {
                cat.Stop();
                overlay.Hide();
                shield.Hide();
            }
            UpdateTray();
        }));
    }

    void UpdateTray()
    {
        tray.Icon = locked ? lockedIcon : unlockedIcon;
        tray.Text = locked ? "CatLock: LOCKED (" + ComboText + ")" : "CatLock: unlocked (" + ComboText + ")";
    }

    void Exit()
    {
        locked = false;
        if (hookThreadId != 0) PostThreadMessage(hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        tray.Visible = false;
        tray.Dispose();
        overlay.Close();
        shield.Close();
        cat.Close();
        ExitThread();
    }
}

// Invisible window covering every monitor while locked. Some touchpad drivers (Alps, Synaptics,
// Elan...) send scroll/gesture messages straight to the window under the cursor, bypassing the
// mouse hook - with this on top, those land here and are ignored. It also takes keyboard focus
// so nothing else is "active" while the cat is on the keys.
class ShieldForm : Form
{
    public ShieldForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.01; // practically invisible, but still catches clicks/scrolls
    }

    public void Cover()
    {
        Bounds = SystemInformation.VirtualScreen;
        Show();
        Activate();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    // Swallow everything that reaches us directly.
    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEWHEEL = 0x20A, WM_MOUSEHWHEEL = 0x20E, WM_HSCROLL = 0x114, WM_VSCROLL = 0x115,
                  WM_GESTURE = 0x119, WM_POINTERFIRST = 0x245, WM_POINTERLAST = 0x257, WM_APPCOMMAND = 0x319;
        int msg = m.Msg;
        if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL || msg == WM_HSCROLL || msg == WM_VSCROLL ||
            msg == WM_GESTURE || msg == WM_APPCOMMAND || (msg >= WM_POINTERFIRST && msg <= WM_POINTERLAST))
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }
}

// Small always-on-top banner shown while locked. Never takes focus.
class OverlayForm : Form
{
    public OverlayForm(string text)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(30, 30, 30);
        Opacity = 0.88;

        var label = new Label
        {
            Text = text,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            AutoSize = true,
            Padding = new Padding(18, 10, 18, 10),
        };
        Controls.Add(label);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    public void ShowOverlay()
    {
        Show();
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - 40);
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TRANSPARENT;
            return cp;
        }
    }
}
