using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// An orange tabby that walks back and forth along the bottom of the screen while locked,
// stopping every so often to sit down and lick its paw.
// Drawn with GDI+ into a per-pixel-alpha layered window, so it's smooth, transparent and click-through.
class CatWalker : Form
{
    const int W = 260, H = 200;   // room in front of the cat for its tail/paws
    const int TopPad = 50;        // extra room above the head (for the fly); drawing is shifted down by this
    const float Speed = 4f;                       // pixels per frame (~30 fps)
    const int WalkMinFrames = 300, WalkMaxFrames = 750;  // walk 10-25 s between activities
    const int LickMinFrames = 120, LickMaxFrames = 210;  // groom for 4-7 s
    const int StretchMinFrames = 100, StretchMaxFrames = 150; // stretch (and yawn) for 3-5 s
    const int ScaredFrames = 75, FleeFrames = 50;        // jump + hiss 2.5 s, then bolt for ~1.7 s
    const int ScaredOdds = 12;                           // 1 in 12 breaks is a fright instead
    const float FleeSpeed = 15f;
    const int SwatMinFrames = 100, SwatMaxFrames = 170;  // swat at a fly for 3-6 s
    const int NapMinFrames = 750, NapMaxFrames = 1050;    // curl up and sleep for 25-35 s
    const int JumpFrames = 45;                           // crouch, leap, land: 1.5 s
    const int JumpMinRise = 120, JumpMaxRise = 320;      // how much higher it lands (pixels)
    const float JumpSpeed = 7f, JumpArc = 60f;           // forward speed and extra height mid-air
    const float Ground = 130;                            // y where the paws touch the ground

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX, CY; public SIZE(int cx, int cy) { CX = cx; CY = cy; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

    static readonly Color Fur = Color.FromArgb(240, 150, 60);
    static readonly Color FurDark = Color.FromArgb(196, 106, 36);
    static readonly Color Pink = Color.FromArgb(245, 160, 170);
    static readonly Color Tongue = Color.FromArgb(232, 92, 112);
    static readonly Color Sock = Color.FromArgb(255, 235, 215);

    enum Eyes { Open, Blink, Content, Wide, LookUp, Sleep }
    enum Activity { Walk, Lick, Stretch, Scared, Flee, Swat, Jump, Nap }

    readonly Timer timer = new Timer { Interval = 33 };
    readonly Bitmap frame = new Bitmap(W, H, PixelFormat.Format32bppArgb);
    readonly Random rng = new Random();
    float x, phase, lickPhase, hop;
    float laneTop;           // window top while standing; a new random height each time the cat re-enters
    float jumpFrom, jumpTo;  // laneTop before/after a jump
    int dir = 1;
    Activity activity;
    int framesLeft;    // frames remaining in the current activity
    int activityFrames; // total length of the current activity

    public CatWalker()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(W, H);
        timer.Tick += (s, e) => Step();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000,
                      WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void Start()
    {
        dir = 1;
        // first break comes a bit sooner
        Begin(Activity.Walk, rng.Next(WalkMinFrames / 2, WalkMaxFrames / 2));
        x = Screen.PrimaryScreen.WorkingArea.Left - W;
        PickLane();
        Show();
        Step();
        timer.Start();
    }

    public void Stop()
    {
        timer.Stop();
        Hide();
    }

    void Begin(Activity a, int frames)
    {
        activity = a;
        framesLeft = activityFrames = frames;
        lickPhase = 0;
    }

    // Progress through the current activity, 0..1.
    float Progress { get { return 1f - framesLeft / (float)activityFrames; } }

    void Step()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        framesLeft--;

        switch (activity)
        {
            case Activity.Walk:
                x += dir * Speed;
                phase += 0.28f;
                TurnAtEdges(wa);

                // Time for a break - but only once fully on screen. Pick one at random;
                // once in a while something spooks the cat instead.
                bool onScreen = x >= wa.Left && x + W <= wa.Right;
                if (framesLeft <= 0 && onScreen)
                {
                    if (rng.Next(ScaredOdds) == 0) Begin(Activity.Scared, ScaredFrames);
                    else
                    {
                        int choice = rng.Next(5);
                        if (choice == 3 && !TryBeginJump(wa)) choice = rng.Next(3);   // no room above: do something else
                        if (choice == 0) Begin(Activity.Lick, rng.Next(LickMinFrames, LickMaxFrames));
                        else if (choice == 1) Begin(Activity.Stretch, rng.Next(StretchMinFrames, StretchMaxFrames));
                        else if (choice == 2) Begin(Activity.Swat, rng.Next(SwatMinFrames, SwatMaxFrames));
                        else if (choice == 4) Begin(Activity.Nap, rng.Next(NapMinFrames, NapMaxFrames));
                    }
                }
                break;

            case Activity.Jump:
                // crouch (0-20%), leap in an arc up to the new height (20-85%), land (85-100%)
                float jt = Progress;
                if (jt >= 0.2f && jt <= 0.85f)
                {
                    float u = (jt - 0.2f) / 0.65f;
                    x += dir * JumpSpeed;
                    laneTop = jumpFrom + (jumpTo - jumpFrom) * u - JumpArc * (float)Math.Sin(Math.PI * u);
                }
                else if (jt > 0.85f) laneTop = jumpTo;
                if (framesLeft <= 0) Begin(Activity.Walk, rng.Next(WalkMinFrames, WalkMaxFrames));
                break;

            case Activity.Nap:
                lickPhase += 1;
                if (framesLeft <= 0) Begin(Activity.Walk, rng.Next(WalkMinFrames, WalkMaxFrames));
                break;

            case Activity.Swat:
                lickPhase += 1;
                if (framesLeft <= 0) Begin(Activity.Walk, rng.Next(WalkMinFrames, WalkMaxFrames));
                break;

            case Activity.Scared:
                // startled hop straight up, then frozen in an arched hiss
                float t = Progress;
                hop = t < 0.25f ? (float)Math.Sin(t / 0.25f * Math.PI) * 32 : 0;
                if (framesLeft <= 0)
                {
                    hop = 0;
                    dir = -dir;                       // spin around and bolt the other way
                    Begin(Activity.Flee, FleeFrames);
                }
                break;

            case Activity.Flee:
                x += dir * FleeSpeed;
                phase += 0.65f;
                TurnAtEdges(wa);
                if (framesLeft <= 0) Begin(Activity.Walk, rng.Next(WalkMinFrames, WalkMaxFrames));
                break;

            case Activity.Lick:
                lickPhase += 0.45f;
                if (framesLeft <= 0) Begin(Activity.Walk, rng.Next(WalkMinFrames, WalkMaxFrames));
                break;

            case Activity.Stretch:
                if (framesLeft <= 0) Begin(Activity.Walk, rng.Next(WalkMinFrames, WalkMaxFrames));
                break;
        }

        Draw();
        Push((int)x, (int)(laneTop - hop) - TopPad);
    }

    bool TryBeginJump(Rectangle wa)
    {
        float target = laneTop - rng.Next(JumpMinRise, JumpMaxRise + 1);
        target = Math.Max(target, wa.Top + 10);
        if (laneTop - target < JumpMinRise * 0.75f) return false;   // already near the top
        jumpFrom = laneTop;
        jumpTo = target;
        Begin(Activity.Jump, JumpFrames);
        return true;
    }

    bool Airborne { get { return activity == Activity.Jump && Progress >= 0.2f && Progress <= 0.85f; } }

    // Once fully off one side, come back from that side at a new random height.
    void TurnAtEdges(Rectangle wa)
    {
        if ((dir > 0 && x > wa.Right) || (dir < 0 && x < wa.Left - W))
        {
            dir = -dir;
            PickLane();
        }
    }

    // Anywhere from near the top of the screen down to just above the "Locked" banner.
    void PickLane()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        int lowest = wa.Bottom - 150 - 95, highest = wa.Top + 10;
        laneTop = lowest > highest ? rng.Next(highest, lowest + 1) : lowest;
    }

    void Draw()
    {
        using (var g = Graphics.FromImage(frame))
        using (var fur = new SolidBrush(Fur))
        using (var pink = new SolidBrush(Pink))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (dir < 0) { g.TranslateTransform(W, 0); g.ScaleTransform(-1, 1); } // face left
            g.TranslateTransform(0, TopPad);

            switch (activity)
            {
                case Activity.Walk: DrawWalking(g, fur, pink, false); break;
                case Activity.Flee: DrawWalking(g, fur, pink, true); break;
                case Activity.Scared: DrawScared(g, fur, pink); break;
                case Activity.Lick: DrawLicking(g, fur, pink); break;
                case Activity.Stretch: DrawStretching(g, fur, pink); break;
                case Activity.Swat: DrawSwatting(g, fur, pink); break;
                case Activity.Jump: DrawJumping(g, fur, pink); break;
                case Activity.Nap: DrawNapping(g, fur, pink); break;
            }
        }
    }

    // Crouch, leap with front legs reaching and back legs pushing off, then land.
    void DrawJumping(Graphics g, Brush fur, Brush pink)
    {
        float t = Progress;
        bool air = Airborne;
        float crouch = t < 0.2f ? Smooth(t / 0.2f) : t > 0.85f ? Smooth((1 - t) / 0.15f) : 0;
        float u = air ? (t - 0.2f) / 0.65f : 0;
        float tilt = air ? -20 + 40 * u : 0;  // nose up on the way up, nose down coming in to land
        float dy = crouch * 10;               // body lowers into the crouch

        var st = g.Save();
        g.TranslateTransform(104, 84);
        g.RotateTransform(tilt);
        g.TranslateTransform(-104, -84);

        float backAng = air ? -60 : 0, frontAng = air ? 60 : 0;
        float legLen = air ? 38 : Ground - (92 + dy);

        Leg(g, FurDark, 72, 92 + dy, backAng, legLen);
        Leg(g, FurDark, 142, 92 + dy, frontAng, legLen);

        using (var p = RoundPen(Fur, 11))
        {
            if (air) g.DrawBezier(p, 55, 80, 35, 78, 20, 72, 6, 62);   // tail streaming out behind
            else g.DrawBezier(p, 55, 80 + dy, 28, 72 + dy, 22, 42 + dy, 38, 24 + dy * 1.5f);
        }

        g.FillEllipse(fur, 45, 62 + dy, 118, 45);
        using (var p = RoundPen(FurDark, 5))
            for (int i = 0; i < 3; i++)
                g.DrawLine(p, 82 + i * 20, 64 + dy, 76 + i * 20, 79 + dy);

        Leg(g, Fur, 82, 92 + dy, backAng + (air ? 8 : 0), legLen);
        Leg(g, Fur, 152, 92 + dy, frontAng - (air ? 8 : 0), legLen);

        DrawHead(g, fur, pink, 168, 58 + dy, 0, Eyes.Open);
        g.Restore(st);
    }

    // Sitting up, looking up, batting at a fly buzzing above its head.
    void DrawSwatting(Graphics g, Brush fur, Brush pink)
    {
        float f = lickPhase;   // frame counter for this activity
        float t = Progress;

        // Swat cycle (30 frames): raise the paw, swipe up fast, hold, bring it down. Every third cycle just watch.
        const float Cycle = 30;
        int n = (int)(f / Cycle);
        float c = (f % Cycle) / Cycle;
        float arm = 0;
        if (n % 3 != 2)
        {
            if (c < 0.4f) arm = 110 * Smooth(c / 0.4f);                       // raise paw in front of the chest
            else if (c < 0.52f) arm = 110 + 50 * ((c - 0.4f) / 0.12f);      // quick swipe up overhead
            else if (c < 0.62f) arm = 160;                                  // hold it up there
            else arm = 160 * (1 - Smooth((c - 0.62f) / 0.38f));             // back down
        }
        bool striking = n % 3 != 2 && c >= 0.4f && c < 0.6f;

        float flick = (float)Math.Sin(f * 0.5) * 8;   // twitchy tail tip
        DrawSitting(g, fur, flick);

        DrawHead(g, fur, pink, 140, 52, -20, Eyes.LookUp);

        // swatting arm: gets longer as it reaches up so the paw clears the head
        const float sx = 146, sy = 86;
        float len = 38 + 38 * Math.Min(1, arm / 160);
        double a = arm * Math.PI / 180;
        float px = sx + (float)(Math.Sin(a) * len), py = sy + (float)(Math.Cos(a) * len);
        using (var p = RoundPen(Fur, 11)) g.DrawLine(p, sx, sy, px, py);
        using (var b = new SolidBrush(Sock)) g.FillEllipse(b, px - 7, py - 5, 14, 11);
        if (striking)
            using (var p = new Pen(Color.FromArgb(150, 255, 255, 255), 2) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                for (int i = -1; i <= 1; i++)
                    g.DrawLine(p, px + 10, py + 4 + i * 6, px + 22, py + 10 + i * 6);   // motion lines

        // the fly: buzzes around above the cat's head, darts up when swatted, and leaves at the end
        float fx = 180 + 14 * (float)Math.Sin(f * 0.31) + 6 * (float)Math.Sin(f * 0.77);
        float fy = -8 + 10 * (float)Math.Sin(f * 0.53);
        if (striking) { fx += 16; fy -= 14; }
        if (t > 0.85f) { float away = (t - 0.85f) / 0.15f; fx += away * 60; fy -= away * 40; }
        bool wingUp = ((int)f & 1) == 0;
        using (var w = new SolidBrush(Color.FromArgb(150, 220, 235, 255)))
        {
            g.FillEllipse(w, fx - 8, fy - (wingUp ? 10 : 6), 8, 7);
            g.FillEllipse(w, fx, fy - (wingUp ? 10 : 6), 8, 7);
        }
        using (var b = new SolidBrush(Color.FromArgb(30, 30, 30)))
            g.FillEllipse(b, fx - 5, fy - 4, 11, 8);
    }

    static float Smooth(float t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }

    // Rotate a point of the standing pose around the hips.
    static PointF Pivot(float px, float py, float angleDeg)
    {
        const float cx = 66, cy = 88;
        double r = angleDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        float dx = px - cx, dy = py - cy;
        return new PointF(cx + (float)(dx * c - dy * s), cy + (float)(dx * s + dy * c));
    }

    // Front legs reach forward, chest drops, rear stays up, tail goes up - and a big yawn at the peak.
    void DrawStretching(Graphics g, Brush fur, Brush pink)
    {
        float t = Progress;
        float s = t < 0.3f ? Smooth(t / 0.3f) : t > 0.7f ? Smooth((1 - t) / 0.3f) : 1f; // ease in, hold, ease out
        float yawn = t > 0.32f && t < 0.68f ? (float)Math.Sin((t - 0.32f) / 0.36f * Math.PI) : 0f;
        float ang = s * 22;   // how far the body tips forward

        PointF hipFar = Pivot(72, 92, ang), hipNear = Pivot(82, 92, ang);
        PointF shFar = Pivot(142, 92, ang), shNear = Pivot(152, 92, ang);

        // far legs
        Leg(g, FurDark, hipFar.X, hipFar.Y, 0, Ground - hipFar.Y);
        Leg(g, FurDark, shFar.X, shFar.Y, ReachAngle(shFar.Y), 38);

        // tail rises up as the cat stretches
        using (var p = RoundPen(Fur, 11))
            g.DrawBezier(p, 55, 80 - s * 4, 28 + s * 14, 72 - s * 22, 22 + s * 10, 42 - s * 26, 38 + s * 4, 24 - s * 18);

        // body + stripes, tipped forward around the hips
        var st = g.Save();
        g.TranslateTransform(66, 88);
        g.RotateTransform(ang);
        g.TranslateTransform(-66, -88);
        g.FillEllipse(fur, 45, 62, 118, 45);
        using (var p = RoundPen(FurDark, 5))
            for (int i = 0; i < 3; i++)
                g.DrawLine(p, 82 + i * 20, 64, 76 + i * 20, 79);
        g.Restore(st);

        // near legs
        Leg(g, Fur, hipNear.X, hipNear.Y, 0, Ground - hipNear.Y);
        Leg(g, Fur, shNear.X, shNear.Y, ReachAngle(shNear.Y), 38);

        // head follows the shoulders and tips up for the yawn
        PointF head = Pivot(168, 58, ang);
        DrawHead(g, fur, pink, head.X, head.Y, -s * 12, yawn > 0.2f ? Eyes.Content : Eyes.Open, yawn);
    }

    // Leg angle that puts a 38px front leg's paw on the ground from a shoulder at height y.
    static float ReachAngle(float y)
    {
        double c = Math.Max(-1, Math.Min(1, (Ground - y) / 38));
        return (float)(Math.Acos(c) * 180 / Math.PI);
    }

    // Normal walk, or (scared) a flat-out run with a puffed-up tail and wide eyes.
    void DrawWalking(Graphics g, Brush fur, Brush pink, bool scared)
    {
        float bob = (float)Math.Abs(Math.Sin(phase)) * (scared ? 6 : 3);
        float swing = (float)Math.Sin(phase) * (scared ? 40 : 28);
        float sway = (float)Math.Sin(phase * 0.5) * 12;

        // far legs (darker, behind the body)
        Leg(g, FurDark, 72, 92 - bob, -swing);
        Leg(g, FurDark, 142, 92 - bob, swing);

        // tail
        if (scared)
            PuffedTail(g, 55, 80 - bob, 30, 66 - bob, 16, 40, 22, 18);
        else
            using (var p = RoundPen(Fur, 11))
                g.DrawBezier(p, 55, 80 - bob, 28, 72 - bob, 22 + sway, 42, 38 + sway, 24);

        // body + stripes
        g.FillEllipse(fur, 45, 62 - bob, 118, 45);
        using (var p = RoundPen(FurDark, 5))
            for (int i = 0; i < 3; i++)
                g.DrawLine(p, 82 + i * 20, 64 - bob, 76 + i * 20, 79 - bob);

        // near legs
        Leg(g, Fur, 82, 92 - bob, swing);
        Leg(g, Fur, 152, 92 - bob, -swing);

        if (scared)
        {
            DrawHead(g, fur, pink, 168, 62 - bob, 6, Eyes.Wide, 0, true);
            return;
        }
        bool blink = (int)(phase * 10) % 90 < 4;
        DrawHead(g, fur, pink, 168, 58 - bob, 0, blink ? Eyes.Blink : Eyes.Open);
    }

    // Halloween-cat pose: back arched high, fur bristling, legs stiff, tail puffed straight up, hissing.
    void DrawScared(Graphics g, Brush fur, Brush pink)
    {
        float t = Progress;
        float jitter = t > 0.25f ? (float)Math.Sin(framesLeft * 2.7) * 1.5f : 0; // trembling
        g.TranslateTransform(jitter, 0);

        // far legs: straight and stiff, splayed a little
        Leg(g, FurDark, 74, 94, -7, Ground - 94);
        Leg(g, FurDark, 140, 94, 7, Ground - 94);

        // puffed tail standing straight up
        PuffedTail(g, 58, 80, 44, 60, 40, 36, 50, 12);

        // arched back
        PointF a0 = new PointF(48, 98), a1 = new PointF(54, 18), a2 = new PointF(152, 18), a3 = new PointF(160, 94);
        using (var path = new GraphicsPath())
        {
            path.AddBezier(a0, a1, a2, a3);
            path.AddBezier(160, 94, 140, 112, 80, 114, 48, 98);
            path.CloseFigure();
            g.FillPath(fur, path);
        }
        // bristling fur along the arch
        for (int i = 1; i < 14; i++)
        {
            PointF p = Bez(i / 14f, a0, a1, a2, a3);
            g.FillPolygon(fur, new[] { new PointF(p.X - 5, p.Y + 3), new PointF(p.X + 1, p.Y - 7), new PointF(p.X + 5, p.Y + 3) });
        }
        using (var p = RoundPen(FurDark, 5))
            for (int i = 0; i < 3; i++)
            {
                PointF s = Bez(0.36f + i * 0.12f, a0, a1, a2, a3);
                g.DrawLine(p, s.X, s.Y + 4, s.X - 3, s.Y + 17);
            }

        // near legs
        Leg(g, Fur, 86, 96, -7, Ground - 96);
        Leg(g, Fur, 152, 96, 7, Ground - 96);

        // head low and forward, ears pinned back, hissing
        DrawHead(g, fur, pink, 172, 70, 8, Eyes.Wide, 0.6f, true);
    }

    static void PuffedTail(Graphics g, float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3)
    {
        PointF p0 = new PointF(x0, y0), p1 = new PointF(x1, y1), p2 = new PointF(x2, y2), p3 = new PointF(x3, y3);
        using (var p = RoundPen(Fur, 18))
            g.DrawBezier(p, p0, p1, p2, p3);
        // fluffy tufts sticking out on both sides
        using (var b = new SolidBrush(Fur))
            for (int i = 2; i < 12; i++)
            {
                PointF c = Bez(i / 12f, p0, p1, p2, p3);
                float side = i % 2 == 0 ? 1 : -1;
                g.FillPolygon(b, new[] { new PointF(c.X - 4, c.Y - 4), new PointF(c.X + side * 13, c.Y - 2), new PointF(c.X - 4, c.Y + 4) });
            }
        using (var p = RoundPen(FurDark, 4))
            for (int i = 0; i < 3; i++)
            {
                PointF c = Bez(0.3f + i * 0.2f, p0, p1, p2, p3);
                g.DrawLine(p, c.X - 6, c.Y, c.X + 6, c.Y - 2);
            }
    }

    static PointF Bez(float u, PointF p0, PointF p1, PointF p2, PointF p3)
    {
        float v = 1 - u;
        float a = v * v * v, b = 3 * v * v * u, c = 3 * v * u * u, d = u * u * u;
        return new PointF(a * p0.X + b * p1.X + c * p2.X + d * p3.X, a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y);
    }

    // Sitting pose minus the head and the "busy" front paw: tail, haunch, upright body, back foot, one front leg.
    static void DrawSitting(Graphics g, Brush fur, float tailSway)
    {
        // tail curled along the ground
        using (var p = RoundPen(Fur, 11))
            g.DrawBezier(p, 92, 128, 60, 136, 34, 134, 30 + tailSway, 116);

        // haunch
        g.FillEllipse(fur, 76, 88, 58, 46);
        using (var p = RoundPen(FurDark, 3))
            g.DrawArc(p, 84, 94, 42, 36, 200, 120);

        // upright body, leaning toward the head
        var st = g.Save();
        g.TranslateTransform(118, 92);
        g.RotateTransform(25);
        g.FillEllipse(fur, -28, -42, 56, 84);
        using (var p = RoundPen(FurDark, 5))
            for (int i = 0; i < 3; i++)
                g.DrawLine(p, -27, -18 + i * 15, -16, -14 + i * 15);
        g.Restore(st);

        // back foot
        using (var b = new SolidBrush(Sock)) g.FillEllipse(b, 112, 128, 22, 8);

        // standing front leg
        using (var p = RoundPen(Fur, 11)) g.DrawLine(p, 142, 84, 145, 128);
        using (var b = new SolidBrush(Sock)) g.FillEllipse(b, 138, 125, 15, 8);
    }

    // Curled up asleep: lying low, head resting on its paws, tail wrapped around the front, breathing, Zzz.
    void DrawNapping(Graphics g, Brush fur, Brush pink)
    {
        float f = lickPhase;
        // settle down over the first second, wake up and lift the head in the last two-thirds of a second
        float down = Math.Min(Smooth(f / 30f), Smooth(framesLeft / 20f));
        bool awake = framesLeft < 20 || f < 15;
        float breathe = (float)Math.Sin(f * 0.1) * 2 * down;

        // body lowers from standing height to lying on the ground
        float bodyTop = 62 + 30 * down;
        float bodyH = 45 + breathe;

        // legs only show while getting up / lying down
        if (down < 0.9f)
        {
            float len = (Ground - 92) * (1 - down) + 4;
            Leg(g, FurDark, 72, Ground - len, 0, len);
            Leg(g, FurDark, 142, Ground - len, 0, len);
        }

        // tail wraps around the front along the ground once it's lying down
        using (var p = RoundPen(Fur, 11))
        {
            if (down > 0.6f) g.DrawBezier(p, 52, bodyTop + 30, 40, 140, 150, 140, 182, 126);
            else g.DrawBezier(p, 55, bodyTop + 18, 28, bodyTop + 10, 22, 42, 38, 24);
        }

        g.FillEllipse(fur, 45, bodyTop - breathe, 125, bodyH);
        using (var p = RoundPen(FurDark, 5))
            for (int i = 0; i < 3; i++)
                g.DrawLine(p, 82 + i * 20, bodyTop + 2 - breathe, 76 + i * 20, bodyTop + 17 - breathe);

        if (down < 0.9f)
        {
            float len = (Ground - 92) * (1 - down) + 4;
            Leg(g, Fur, 82, Ground - len, 0, len);
            Leg(g, Fur, 152, Ground - len, 0, len);
        }

        // front paws tucked under the chin
        if (down > 0.6f)
        using (var b = new SolidBrush(Sock))
        {
            g.FillEllipse(b, 158, 122, 18, 9);
            g.FillEllipse(b, 172, 125, 18, 9);
        }

        float hy = 58 + 46 * down;
        DrawHead(g, fur, pink, 172, hy, 8 * down, awake ? Eyes.Open : Eyes.Sleep);

        // Zzz floating up and fading out, staggered
        if (!awake)
            using (var font = new Font("Segoe UI", 10f, FontStyle.Bold))
                for (int i = 0; i < 3; i++)
                {
                    float u = ((f + i * 25) % 75) / 75f;     // 0..1 over 2.5 s
                    int alpha = (int)(230 * Math.Sin(u * Math.PI));
                    float size = 0.8f + u * 0.9f;
                    var st = g.Save();
                    g.TranslateTransform(196 + u * 30, hy - 20 - u * 70);
                    g.ScaleTransform(size, size);
                    if (dir < 0) { g.ScaleTransform(-1, 1); g.TranslateTransform(-12, 0); } // keep the letters readable
                    using (var b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                        g.DrawString("z", font, b, 0, 0);
                    g.Restore(st);
                }
    }
    void DrawLicking(Graphics g, Brush fur, Brush pink)
    {
        // Lick in bursts: three licks, then a short pause, repeat.
        const double TwoPi = Math.PI * 2;
        double cycle = lickPhase % (TwoPi * 5);
        float lick = cycle < TwoPi * 3 ? (float)Math.Max(0, Math.Sin(cycle)) : 0f;
        bool pausing = cycle >= TwoPi * 3;
        float tailSway = (float)Math.Sin(lickPhase * 0.15) * 6;

        DrawSitting(g, fur, tailSway);
        GraphicsState st;

        // head, tilted down toward the paw and nodding with each lick
        float hx = 163, hy = 48 + lick * 3;
        float tilt = pausing ? 4 : 18;
        DrawHead(g, fur, pink, hx, hy, tilt, pausing ? Eyes.Open : Eyes.Content);

        // raised paw, coming up to the mouth on each lick
        float pawX = 166, pawY = 72 - lick * 5;
        using (var p = RoundPen(Fur, 11))
        {
            g.DrawLine(p, 148, 86, 160, 98);
            g.DrawLine(p, 160, 98, pawX, pawY + 4);
        }
        using (var b = new SolidBrush(Sock)) g.FillEllipse(b, pawX - 7, pawY - 5, 14, 11);

        // tongue, drawn on top of the paw
        if (lick > 0.35f)
        {
            st = g.Save();
            g.TranslateTransform(hx, hy);
            g.RotateTransform(tilt);
            using (var b = new SolidBrush(Tongue))
                g.FillEllipse(b, -3, 8, 7, 6 + lick * 5);
            g.Restore(st);
        }
    }

    // Tray icon: the cat's face, with a little padlock badge when locked.
    public static Icon MakeIcon(bool locked)
    {
        using (var bmp = RenderFace(32, locked))
            return Icon.FromHandle(bmp.GetHicon());
    }

    // The cat's face on a transparent square of any size (also used to generate cat.ico).
    public static Bitmap RenderFace(int size, bool locked)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var fur = new SolidBrush(Fur))
        using (var pink = new SolidBrush(Pink))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 32f, size / 32f);   // everything below is in 32x32 units
            var st = g.Save();
            g.TranslateTransform(16, 19);
            g.ScaleTransform(0.5f, 0.5f);
            DrawHead(g, fur, pink, 0, 0, 0, Eyes.Open);
            g.Restore(st);

            if (locked)
            {
                using (var p = new Pen(Color.FromArgb(60, 60, 60), 2.5f))
                    g.DrawArc(p, 21, 15, 8, 9, 180, 180);           // shackle
                using (var b = new SolidBrush(Color.FromArgb(60, 60, 60)))
                    g.FillRectangle(b, 19, 20, 12, 10);            // body
                using (var b = new SolidBrush(Color.FromArgb(250, 210, 80)))
                    g.FillRectangle(b, 24, 23, 2, 4);              // keyhole
            }
        }
        return bmp;
    }

    // Head drawn around (cx, cy), rotated by angle degrees.
    static void DrawHead(Graphics g, Brush fur, Brush pink, float cx, float cy, float angle, Eyes eyes, float yawn = 0, bool earsFlat = false)
    {
        var st = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(angle);

        // ears (pinned back sideways when scared)
        if (earsFlat)
        {
            g.FillPolygon(fur, new[] { new PointF(-20, -6), new PointF(-38, -22), new PointF(-8, -20) });
            g.FillPolygon(fur, new[] { new PointF(8, -20), new PointF(38, -22), new PointF(20, -6) });
            g.FillPolygon(pink, new[] { new PointF(-19, -11), new PointF(-31, -20), new PointF(-12, -18) });
            g.FillPolygon(pink, new[] { new PointF(12, -18), new PointF(31, -20), new PointF(19, -11) });
        }
        else
        {
            g.FillPolygon(fur, new[] { new PointF(-22, -8), new PointF(-16, -38), new PointF(-2, -20) });
            g.FillPolygon(fur, new[] { new PointF(2, -20), new PointF(16, -38), new PointF(22, -8) });
            g.FillPolygon(pink, new[] { new PointF(-17, -14), new PointF(-15, -31), new PointF(-7, -20) });
            g.FillPolygon(pink, new[] { new PointF(7, -20), new PointF(15, -31), new PointF(17, -14) });
        }
        g.FillEllipse(fur, -26, -24, 52, 46);

        // eyes
        switch (eyes)
        {
            case Eyes.Open:
                g.FillEllipse(Brushes.Black, -13, -9, 8, 11);
                g.FillEllipse(Brushes.Black, 5, -9, 8, 11);
                g.FillEllipse(Brushes.White, -11, -7, 3, 3);
                g.FillEllipse(Brushes.White, 7, -7, 3, 3);
                break;
            case Eyes.Blink:
                using (var p = new Pen(Color.Black, 2))
                {
                    g.DrawLine(p, -13, -3, -5, -3);
                    g.DrawLine(p, 5, -3, 13, -3);
                }
                break;
            case Eyes.LookUp: // open, pupils raised
                g.FillEllipse(Brushes.White, -15, -12, 11, 13);
                g.FillEllipse(Brushes.White, 4, -12, 11, 13);
                g.FillEllipse(Brushes.Black, -13, -13, 8, 9);
                g.FillEllipse(Brushes.Black, 6, -13, 8, 9);
                g.FillEllipse(Brushes.White, -11, -12, 3, 3);
                g.FillEllipse(Brushes.White, 8, -12, 3, 3);
                break;
            case Eyes.Sleep: // peacefully closed
                using (var p = RoundPen(Color.Black, 2))
                {
                    g.DrawArc(p, -14, -7, 10, 7, 0, 180);
                    g.DrawArc(p, 4, -7, 10, 7, 0, 180);
                }
                break;
            case Eyes.Wide: // big startled eyes with tiny pupils
                g.FillEllipse(Brushes.White, -16, -12, 13, 15);
                g.FillEllipse(Brushes.White, 3, -12, 13, 15);
                using (var p = new Pen(Color.Black, 1.5f))
                {
                    g.DrawEllipse(p, -16, -12, 13, 15);
                    g.DrawEllipse(p, 3, -12, 13, 15);
                }
                g.FillEllipse(Brushes.Black, -11, -7, 4, 5);
                g.FillEllipse(Brushes.Black, 8, -7, 4, 5);
                break;
            case Eyes.Content: // happy closed eyes
                using (var p = RoundPen(Color.Black, 2))
                {
                    g.DrawArc(p, -14, -6, 10, 7, 180, 180);
                    g.DrawArc(p, 4, -6, 10, 7, 180, 180);
                }
                break;
        }

        // nose, mouth, whiskers
        if (yawn > 0.05f)
        {
            // wide-open mouth with a little tongue
            float h = 3 + yawn * 13;
            using (var b = new SolidBrush(Color.FromArgb(120, 35, 45))) g.FillEllipse(b, -7, 7, 14, h);
            using (var b = new SolidBrush(Tongue)) g.FillEllipse(b, -5, 7 + h * 0.55f, 10, h * 0.45f);
            using (var b = new SolidBrush(Color.White))
            {
                g.FillPolygon(b, new[] { new PointF(-6, 9), new PointF(-4, 9), new PointF(-5, 9 + yawn * 3) }); // fangs
                g.FillPolygon(b, new[] { new PointF(4, 9), new PointF(6, 9), new PointF(5, 9 + yawn * 3) });
            }
        }
        g.FillPolygon(pink, new[] { new PointF(-4, 5), new PointF(4, 5), new PointF(0, 9) });
        if (yawn <= 0.05f)
            using (var p = new Pen(Color.FromArgb(90, 50, 20), 1.5f))
            {
                g.DrawArc(p, -6, 6, 6, 6, 0, 180);
                g.DrawArc(p, 0, 6, 6, 6, 0, 180);
            }
        using (var p = new Pen(Color.FromArgb(220, 255, 255, 255), 1.2f))
        {
            g.DrawLine(p, -8, 7, -34, 3);
            g.DrawLine(p, -8, 9, -34, 12);
            g.DrawLine(p, 8, 7, 34, 3);
            g.DrawLine(p, 8, 9, 34, 12);
        }
        g.Restore(st);
    }

    static Pen RoundPen(Color c, float width)
    {
        return new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
    }

    static void Leg(Graphics g, Color c, float x, float y, float angleDeg, float len = 38)
    {
        double a = angleDeg * Math.PI / 180;
        float ex = x + (float)(Math.Sin(a) * len), ey = y + (float)(Math.Cos(a) * len);
        using (var p = RoundPen(c, 11))
            g.DrawLine(p, x, y, ex, ey);
        using (var b = new SolidBrush(Sock))
            g.FillEllipse(b, ex - 7, ey - 3, 14, 8); // white socks
    }

    // Copy the frame to the layered window at the given screen position (per-pixel alpha).
    void Push(int left, int top)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = frame.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            var dst = new POINT(left, top);
            var size = new SIZE(W, H);
            var src = new POINT(0, 0);
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
