// DeskNotes 2 — hand-drawn sticky note for the Windows desktop.
// Tabs (to-do lists / notes), click an item to open details, colored tags, fully customizable look.
// No taskbar button, no tray icon, not in Alt+Tab, hidden from screen capture/streams, 0% CPU when idle.
// Data: %APPDATA%\DeskNotes\ (data.json, settings.json, fonts\)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using IOPath = System.IO.Path;
using IOFile = System.IO.File;
using IODir = System.IO.Directory;
using SPath = System.Windows.Shapes.Path;
using Ellipse = System.Windows.Shapes.Ellipse;
using RectShape = System.Windows.Shapes.Rectangle;

[assembly: System.Reflection.AssemblyTitle("DeskNotes")]
[assembly: System.Reflection.AssemblyProduct("DeskNotes")]
[assembly: System.Reflection.AssemblyDescription("Рисованный стикер на рабочем столе")]
[assembly: System.Reflection.AssemblyVersion("2.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.1.0.0")]

// ---------------- data ----------------
[DataContract] class Sub
{
    [DataMember] public string Text;
    [DataMember] public bool Done;
    [OnDeserializing] void D(StreamingContext c) { Text = ""; }
}

[DataContract] class Item
{
    [DataMember] public string Title;
    [DataMember] public bool Done;
    [DataMember] public int Tag;
    [DataMember] public string Details;
    [DataMember] public List<Sub> Subs;
    [DataMember] public DateTime Created;
    public Item() { Init(); }
    [OnDeserializing] void D(StreamingContext c) { Init(); }
    void Init() { Title = ""; Tag = -1; Details = ""; Subs = new List<Sub>(); Created = DateTime.Now; }
}

[DataContract] class Tab
{
    [DataMember] public string Name;
    [DataMember] public bool Checklist;
    [DataMember] public List<Item> Items;
    [DataMember] public bool Drawing;
    [DataMember] public string InkData;
    public Tab() { Init(); }
    [OnDeserializing] void D(StreamingContext c) { Init(); }
    void Init() { Name = "Вкладка"; Checklist = true; Items = new List<Item>(); }
}

[DataContract] class Store
{
    [DataMember] public List<Tab> Tabs;
    [DataMember] public int Active;
    [DataMember] public bool DrawTabAdded;
    public Store() { Init(); }
    [OnDeserializing] void D(StreamingContext c) { Init(); }
    void Init() { Tabs = new List<Tab>(); }
}

[DataContract] class Cfg
{
    [DataMember] public double X, Y, W, H;
    [DataMember] public string Paper, Ink, Font, Pin, TagStyle, Hotkey;
    [DataMember] public double FontSize, Rotation, Shadow;
    [DataMember] public List<string> TagColors;
    [DataMember] public bool HideFromCapture;
    [DataMember] public int Smooth;
    public Cfg() { Init(); }
    [OnDeserializing] void D(StreamingContext c) { Init(); }
    void Init()
    {
        X = Y = -99999; W = 340; H = 460;
        Paper = "#3A3B3F"; Ink = "#ECEAE4"; Font = "Caveat"; Pin = "push"; TagStyle = "ink"; Hotkey = "Ctrl+Alt+N";
        FontSize = 22; Rotation = -1.5; Shadow = 10;
        TagColors = new List<string> { "#FF6464", "#FFB020", "#46CC78", "#4A90FF", "#B97CFF" };
        HideFromCapture = true;
        Smooth = 2;
    }
}

// ---------------- window ----------------
class Sticky : Window
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr p, int infoClass, ref PowerThrottling info, int size);
    [StructLayout(LayoutKind.Sequential)] struct PowerThrottling { public uint Version, ControlMask, StateMask; }

    static Geometry Frozen(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }
    static readonly Geometry BoxGeo = Frozen(BOX), CheckGeo = Frozen(CHECK), ScribbleGeo = Frozen(SCRIBBLE);

    // While the sticker is in the background (you're playing, working, streaming) the process runs at idle
    // priority with Windows 11 "efficiency mode", so it never competes with anything. When you open it,
    // it goes back to normal priority for snappy typing.
    static void BackgroundMode(bool on)
    {
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = on ? System.Diagnostics.ProcessPriorityClass.Idle : System.Diagnostics.ProcessPriorityClass.Normal; } catch { }
        try
        {
            var st = new PowerThrottling { Version = 1, ControlMask = 1, StateMask = on ? 1u : 0u }; // EXECUTION_SPEED = EcoQoS
            SetProcessInformation(GetCurrentProcess(), 4, ref st, Marshal.SizeOf(typeof(PowerThrottling)));
        }
        catch { }
    }

    const int HOTKEY_ID = 1, WM_HOTKEY = 0x0312;
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string BOX = "M3,3.6 C7.5,3 12.5,3.3 16.8,3.4 C17.3,8 17,12.5 16.7,16.9 C12,17.3 7.4,17 3.3,16.7 C3.1,12.2 2.8,7.9 3,3.6 Z";
    const string CHECK = "M5,9.6 C6.8,11.2 8,13.2 9,15.2 C11,10.5 14.3,5.6 19.5,1.2";
    const string SCRIBBLE = "M0,3 C10,1 25,4.5 40,2.5 C55,0.8 70,3.5 100,2";

    static readonly string Dir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskNotes");
    static string F(string n) { return IOPath.Combine(Dir, n); }
    static readonly CultureInfo Ru = new CultureInfo("ru-RU");

    Store store; Cfg cfg;
    IntPtr hwnd, prevFg;
    Item expanded;
    Span expandedRun;
    TextBox focusAfter;
    string focusAfterKind;
    Tuple<Tab, int, Item> lastDeleted;

    SolidColorBrush ink, paperBr; FontFamily font; double fs;
    Grid root, paperGrid, addHost;
    Border paper;
    ContentControl pinHost;
    StackPanel tabsPanel;
    TextBlock menuBtn;
    ScrollViewer scroll;
    StackPanel list;
    TextBox addBox;
    DispatcherTimer saveTimer;
    uint hkMods = 0x2 | 0x1, hkVk;

    [STAThread]
    static void Main()
    {
        bool created;
        using (var m = new Mutex(true, "DeskNotes_SingleInstance_7f3a", out created))
        {
            if (!created) return;
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly; // never touches the GPU
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new Sticky());
        }
    }

    public Sticky()
    {
        IODir.CreateDirectory(Dir);
        IODir.CreateDirectory(F("fonts"));
        LoadAll();

        Title = "DeskNotes";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Width = Math.Max(240, cfg.W); Height = Math.Max(220, cfg.H);
        var wa = SystemParameters.WorkArea;
        if (cfg.X < -9999 || cfg.X > SystemParameters.VirtualScreenWidth + SystemParameters.VirtualScreenLeft - 60 || cfg.Y < SystemParameters.VirtualScreenTop - 20)
        { Left = wa.Right - Width - 20; Top = wa.Top + 30; }
        else { Left = cfg.X; Top = cfg.Y; }

        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        saveTimer.Tick += delegate { Flush(); };

        BuildChrome();
        ApplyLook();

        SourceInitialized += OnSrcInit;
        Loaded += delegate { SendToBottom(); BackgroundMode(true); TrimMemory(); };
        Activated += delegate { BackgroundMode(false); };
        Deactivated += delegate { SendToBottom(); Flush(); BackgroundMode(true); TrimMemory(); };
        LocationChanged += delegate { cfg.X = Left; cfg.Y = Top; MarkDirty(); };
        SizeChanged += delegate { cfg.W = Width; cfg.H = Height; MarkDirty(); };
        PreviewKeyDown += OnKey;
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent, new RoutedEventHandler((s, e) => HideFromCapture((Visual)s)));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ToolTip), System.Windows.Controls.ToolTip.OpenedEvent, new RoutedEventHandler((s, e) => HideFromCapture((Visual)s)));
        Closing += delegate { Flush(); };
    }

    // ---------------- win32 behaviour ----------------
    void OnSrcInit(object s, EventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, new IntPtr((ex | 0x80) & ~0x40000L)); // tool window: no taskbar, no Alt+Tab
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero) SetWindowLongPtr(hwnd, -8, progman); // owned by desktop: survives Win+D
        ApplyCaptureHide();
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        if (!ParseHotkey(cfg.Hotkey, out hkMods, out hkVk)) { cfg.Hotkey = "Ctrl+Alt+N"; ParseHotkey(cfg.Hotkey, out hkMods, out hkVk); }
        if (!RegisterHotKey(hwnd, HOTKEY_ID, hkMods | 0x4000, hkVk))
            MessageBox.Show("Сочетание " + cfg.Hotkey + " занято другой программой.\nПоменяй его в " + F("settings.json") + " (\"Hotkey\").", "DeskNotes");
    }

    void ApplyCaptureHide()
    {
        if (hwnd != IntPtr.Zero) SetWindowDisplayAffinity(hwnd, cfg.HideFromCapture ? 0x11u : 0u); // WDA_EXCLUDEFROMCAPTURE
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_HOTKEY && w.ToInt32() == HOTKEY_ID) { OnHotkey(); handled = true; }
        return IntPtr.Zero;
    }

    void OnHotkey()
    {
        if (GetForegroundWindow() == hwnd) { SendBack(); return; }
        prevFg = GetForegroundWindow();
        Show();
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        SetForegroundWindow(hwnd);
        Activate();
        FocusLater(addBox);
    }

    void SendBack()
    {
        IntPtr p = prevFg; prevFg = IntPtr.Zero;
        if (p != IntPtr.Zero && p != hwnd && IsWindow(p)) SetForegroundWindow(p);
        else SetForegroundWindow(FindWindow("Progman", null));
        SendToBottom();
    }

    void SendToBottom()
    {
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    static void TrimMemory()
    {
        try { GC.Collect(); GC.WaitForPendingFinalizers(); SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1)); } catch { }
    }

    void OnKey(object s, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var tb = Keyboard.FocusedElement as TextBox;
        if (e.Key == Key.Escape && tb != null && "popup".Equals(tb.Tag)) return; // the little popup closes itself
        if (e.Key == Key.Escape)
        {
            if (tb != null && "rename".Equals(tb.Tag)) { tb.Tag = "cancel"; RenderTabs(); }
            else if (expanded != null) { expanded = null; RenderItems(); FocusLater(addBox); }
            else SendBack();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Tab)
        {
            int n = store.Tabs.Count;
            SwitchTab(((store.Active + ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1)) % n + n) % n);
            e.Handled = true;
        }
        else if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            int i = e.Key - Key.D1;
            if (i < store.Tabs.Count) SwitchTab(i);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.N) { FocusLater(addBox); e.Handled = true; }
        else if (ctrl && e.Key == Key.M) { StartDictation(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z && Cur.Drawing) { UndoStroke(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z && lastDeleted != null && (tb == null || tb.Text.Length == 0 || tb == addBox))
        {
            var d = lastDeleted; lastDeleted = null;
            if (store.Tabs.Contains(d.Item1)) { d.Item1.Items.Insert(Math.Min(d.Item2, d.Item1.Items.Count), d.Item3); MarkDirty(); RenderItems(); }
            e.Handled = true;
        }
    }

    // ---------------- look ----------------
    static SolidColorBrush B(string hex, byte alpha = 255)
    {
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { c = Colors.White; }
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
    SolidColorBrush InkA(byte a) { var c = ink.Color; var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze(); return b; }
    SolidColorBrush TagBrush(Item it) { return it.Tag >= 0 && it.Tag < cfg.TagColors.Count ? B(cfg.TagColors[it.Tag]) : null; }

    List<FontFamily> customFonts;
    List<FontFamily> CustomFonts()
    {
        if (customFonts != null) return customFonts;
        customFonts = new List<FontFamily>();
        try
        {
            if (IODir.EnumerateFiles(F("fonts")).Any(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)))
                customFonts.AddRange(Fonts.GetFontFamilies(new Uri(F("fonts") + "\\")));
        }
        catch { }
        return customFonts;
    }
    static string FamName(FontFamily ff)
    {
        string n = ff.FamilyNames.Values.FirstOrDefault();
        return n ?? ff.Source.Split('#').Last();
    }
    // checks one font directly instead of enumerating every installed font (that was slow and memory-hungry)
    static bool HasSystemFont(string name)
    {
        GlyphTypeface g;
        try { return new Typeface(name).TryGetGlyphTypeface(out g); } catch { return false; }
    }
    List<string> AvailableFonts()
    {
        var res = CustomFonts().Select(FamName).Distinct().ToList();
        foreach (var n in new[] { "Ink Free", "Segoe Print", "Segoe Script", "Comic Sans MS", "Segoe UI" })
            if (!res.Contains(n) && HasSystemFont(n)) res.Add(n);
        return res;
    }
    FontFamily ResolveFont(string name)
    {
        foreach (var ff in CustomFonts()) if (FamName(ff) == name) return ff;
        if (HasSystemFont(name)) return new FontFamily(name);
        return new FontFamily("Ink Free, Segoe Print");
    }

    void BuildChrome()
    {
        root = new Grid { Margin = new Thickness(22, 26, 22, 22) };
        paper = new Border { CornerRadius = new CornerRadius(3, 3, 5, 18), RenderTransformOrigin = new Point(.5, .5), Padding = new Thickness(20, 16, 16, 10) };
        paperGrid = new Grid();
        paperGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        paperGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        paperGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var tabsRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent, LastChildFill = true };
        menuBtn = new TextBlock { Text = "⋯", Cursor = Cursors.Hand, Opacity = .55, Padding = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, FontFamily = new FontFamily("Segoe UI") };
        menuBtn.MouseLeftButtonDown += (s, e) => { e.Handled = true; var m = MainMenu(); m.PlacementTarget = menuBtn; m.IsOpen = true; };
        DockPanel.SetDock(menuBtn, Dock.Right);
        // all tabs stay on one line; if they don't fit, the row shrinks a little instead of wrapping
        tabsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        tabsRow.Children.Add(menuBtn);
        tabsRow.Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = tabsPanel });

        scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        list = new StackPanel();
        scroll.Content = list;
        contentHost = new Border { Child = scroll };
        Grid.SetRow(contentHost, 1);

        addHost = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetRow(addHost, 2);

        var grip = new SPath { Data = Geometry.Parse("M10,0 L0,10 M10,5 L5,10"), Width = 12, Height = 12, Margin = new Thickness(0, 0, -8, -4),
                               HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE, Opacity = .35, Fill = Brushes.Transparent };
        Grid.SetRow(grip, 2);
        Point start = new Point(); Size startSize = new Size();
        grip.MouseLeftButtonDown += (s, e) => { e.Handled = true; start = PointToScreen(e.GetPosition(this)); startSize = new Size(Width, Height); grip.CaptureMouse(); };
        grip.MouseMove += (s, e) =>
        {
            if (!grip.IsMouseCaptured) return;
            var p = PointToScreen(e.GetPosition(this));
            var src = PresentationSource.FromVisual(this);
            double k = src != null ? src.CompositionTarget.TransformToDevice.M11 : 1;
            Width = Math.Max(240, startSize.Width + (p.X - start.X) / k);
            Height = Math.Max(220, startSize.Height + (p.Y - start.Y) / k);
        };
        grip.MouseLeftButtonUp += (s, e) => grip.ReleaseMouseCapture();
        gripRef = grip;

        paperGrid.Children.Add(tabsRow);
        paperGrid.Children.Add(contentHost);
        paperGrid.Children.Add(addHost);
        paperGrid.Children.Add(grip);
        paper.Child = paperGrid;
        paper.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } } };

        pinHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Focusable = false };
        root.Children.Add(paper);
        root.Children.Add(pinHost);
        Content = root;
    }
    SPath gripRef;
    Border contentHost;
    InkCanvas canvas;
    string drawColor;
    double drawSize = 4;
    bool erasing;

    void ApplyLook()
    {
        ink = B(cfg.Ink); paperBr = B(cfg.Paper);
        font = ResolveFont(cfg.Font); fs = cfg.FontSize;
        paper.Background = paperBr;
        paper.RenderTransform = new RotateTransform(cfg.Rotation);
        BuildShadow();
        TextElement.SetFontFamily(paper, font);
        TextElement.SetFontSize(paper, fs);
        TextElement.SetForeground(paper, ink);
        menuBtn.FontSize = fs * .9;
        gripRef.Stroke = ink;
        pinHost.Content = MakePin(cfg.Pin);
        RebuildAll();
    }

    // Soft shadow from a few stacked translucent shapes. A blur effect looks the same but in software
    // rendering it is recomputed on every keystroke; these layers cost almost nothing to draw.
    void BuildShadow()
    {
        if (shadowHost == null)
        {
            shadowHost = new Grid { IsHitTestVisible = false, RenderTransformOrigin = new Point(.5, .5) };
            root.Children.Insert(0, shadowHost);
        }
        shadowHost.Children.Clear();
        shadowHost.RenderTransform = paper.RenderTransform;
        if (cfg.Shadow <= 0) return;
        const int layers = 6;
        double spread = cfg.Shadow * .9, depth = cfg.Shadow * .45;
        var shade = B("#000000", (byte)Math.Min(60, 14 + cfg.Shadow * 2.2));
        for (int i = 0; i < layers; i++)
        {
            double k = spread * (i + 1) / layers;
            shadowHost.Children.Add(new Border
            {
                Background = shade, Opacity = 1.0 / (i + 1.6),
                CornerRadius = new CornerRadius(3 + k, 3 + k, 5 + k, 18 + k),
                Margin = new Thickness(-k + 2, -k + depth, -k + 2, -k - depth)
            });
        }
    }
    Grid shadowHost;

    UIElement MakePin(string kind)
    {
        pinHost.HorizontalAlignment = HorizontalAlignment.Center;
        if (kind == "push")
        {
            var g = new Grid { Width = 20, Height = 20, Margin = new Thickness(0, -12, 0, 0) };
            g.Children.Add(new Ellipse { Fill = B("#000000", 70), Margin = new Thickness(2.5, 3.5, -2.5, -3.5) });
            g.Children.Add(new Ellipse { Fill = B("#D8433F") });
            g.Children.Add(new Ellipse { Fill = B("#FFFFFF", 110), Width = 6, Height = 6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4.5, 4, 0, 0) });
            return g;
        }
        if (kind == "tape")
            return new RectShape { Width = 92, Height = 26, Fill = B("#EFE9D8", 150), Margin = new Thickness(0, -14, 0, 0), RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform(-3) };
        if (kind == "clip")
        {
            pinHost.HorizontalAlignment = HorizontalAlignment.Left;
            var c = new Canvas { Width = 20, Height = 48, Margin = new Thickness(26, -22, 0, 0), RenderTransform = new RotateTransform(-6) };
            c.Children.Add(new RectShape { Width = 18, Height = 46, RadiusX = 9, RadiusY = 9, Stroke = B("#A2AAB5"), StrokeThickness = 2.5 });
            var inner = new RectShape { Width = 10, Height = 34, RadiusX = 5, RadiusY = 5, Stroke = B("#A2AAB5"), StrokeThickness = 2.5 };
            Canvas.SetLeft(inner, 4); Canvas.SetTop(inner, 9);
            c.Children.Add(inner);
            return c;
        }
        return null;
    }

    void RebuildAll() { RenderTabs(); RenderAdd(); RenderItems(); }

    // ---------------- tabs ----------------
    Tab Cur { get { if (store.Active < 0 || store.Active >= store.Tabs.Count) store.Active = 0; return store.Tabs[store.Active]; } }

    void SwitchTab(int i)
    {
        if (i == store.Active) return;
        store.Active = i; expanded = null; MarkDirty();
        RebuildAll();
        FocusLater(addBox);
    }

    void RenderTabs()
    {
        tabsPanel.Children.Clear();
        for (int i = 0; i < store.Tabs.Count; i++)
        {
            int idx = i;
            var tab = store.Tabs[i];
            bool active = i == store.Active;
            var sp = new StackPanel { Margin = new Thickness(0, 0, 16, 2), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var name = new TextBlock { Text = tab.Name, FontSize = fs * 1.05, FontWeight = FontWeights.Bold, Opacity = active ? 1 : .45 };
            sp.Children.Add(name);
            if (active)
            {
                var line = new SPath { Data = ScribbleGeo, Stretch = Stretch.Fill, Height = 4, Stroke = ink, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Margin = new Thickness(0, -1, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                line.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = name });
                sp.Children.Add(line);
            }
            sp.MouseLeftButtonDown += (s, e) => { e.Handled = true; if (e.ClickCount == 2) StartRename(idx); else SwitchTab(idx); };
            sp.ContextMenu = new ContextMenu(); // built only when actually opened
            sp.ContextMenuOpening += delegate { sp.ContextMenu = TabMenu(idx); };
            tabsPanel.Children.Add(sp);
        }
        var plus = new TextBlock { Text = "+", FontSize = fs * 1.05, Opacity = .4, Cursor = Cursors.Hand, ToolTip = "Новая вкладка" };
        plus.MouseLeftButtonDown += (s, e) => { e.Handled = true; var m = NewTabMenu(); m.PlacementTarget = plus; m.IsOpen = true; };
        tabsPanel.Children.Add(plus);
    }

    void AddTab(string kind)
    {
        store.Tabs.Add(new Tab { Name = kind == "draw" ? "Рисунок" : kind == "notes" ? "Заметки" : "Дела", Checklist = kind == "list", Drawing = kind == "draw" });
        store.Active = store.Tabs.Count - 1; expanded = null; MarkDirty();
        RebuildAll();
        StartRename(store.Active);
    }

    void StartRename(int i)
    {
        if (i >= tabsPanel.Children.Count) return;
        var sp = tabsPanel.Children[i] as StackPanel;
        var tab = store.Tabs[i];
        var box = HandBox(tab.Name, fs * 1.05, false);
        box.FontWeight = FontWeights.Bold; box.MinWidth = 60; box.Tag = "rename";
        bool done = false;
        Action commit = () =>
        {
            if (done) return; done = true;
            if (!"cancel".Equals(box.Tag) && box.Text.Trim().Length > 0) { tab.Name = box.Text.Trim(); MarkDirty(); }
            RenderTabs();
        };
        box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; commit(); FocusLater(addBox); } };
        box.LostKeyboardFocus += (s, e) => commit();
        sp.Children.Clear();
        sp.Children.Add(box);
        FocusLater(box, true);
    }

    ContextMenu TabMenu(int i)
    {
        var tab = store.Tabs[i];
        var m = new ContextMenu();
        m.Items.Add(MI("Переименовать", () => StartRename(i)));
        if (!tab.Drawing)
        {
            m.Items.Add(MI("Список дел (с галочками)", () => { tab.Checklist = true; MarkDirty(); RenderItems(); RenderAdd(); }, tab.Checklist));
            m.Items.Add(MI("Заметки (без галочек)", () => { tab.Checklist = false; MarkDirty(); RenderItems(); RenderAdd(); }, !tab.Checklist));
        }
        m.Items.Add(new Separator());
        if (i > 0) m.Items.Add(MI("Сдвинуть левее", () => MoveTab(i, -1)));
        if (i < store.Tabs.Count - 1) m.Items.Add(MI("Сдвинуть правее", () => MoveTab(i, 1)));
        if (!tab.Drawing) m.Items.Add(MI("Удалить выполненные", () => { tab.Items.RemoveAll(x => x.Done); MarkDirty(); RenderItems(); }));
        if (store.Tabs.Count > 1)
        {
            var del = new MenuItem { Header = "Удалить вкладку" };
            del.Items.Add(MI("Да, удалить «" + tab.Name + "» (" + tab.Items.Count + " зап.)", () =>
            {
                store.Tabs.RemoveAt(i);
                if (store.Active >= store.Tabs.Count) store.Active = store.Tabs.Count - 1;
                expanded = null; MarkDirty(); RebuildAll();
            }));
            m.Items.Add(del);
        }
        return m;
    }

    void MoveTab(int i, int d)
    {
        var t = store.Tabs[i]; store.Tabs.RemoveAt(i); store.Tabs.Insert(i + d, t);
        if (store.Active == i) store.Active = i + d; else if (store.Active == i + d) store.Active = i;
        MarkDirty(); RenderTabs();
    }

    // ---------------- items ----------------
    void RenderAdd()
    {
        addHost.Children.Clear();
        addHost.ColumnDefinitions.Clear();
        if (Cur.Drawing) { addBox = null; addHost.Children.Add(DrawToolbar()); return; }
        addHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var plus = new TextBlock { Text = "+", Opacity = .5, Margin = new Thickness(2, 0, 9, 0) };
        addHost.Children.Add(plus);
        addHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var host = Placeholder(HandBox("", fs * .95, false), dictating ? "слушаю… говори" : Cur.Checklist ? "новое дело" : "новая заметка");
        addBox = (TextBox)host.Tag;
        addBox.Opacity = .85;

        var mic = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = fs * .72,
            Foreground = dictating ? B(cfg.TagColors[0]) : ink, Opacity = dictating ? 1 : .55, Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            ToolTip = dictating ? "Слушаю. Пауза 3 секунды, Enter или клик — готово" : "Надиктовать (Ctrl+M). Несколько дел за раз разделятся сами"
        };
        mic.MouseLeftButtonDown += (s, e) => { e.Handled = true; StartDictation(); };
        Grid.SetColumn(mic, 2);
        addHost.Children.Add(mic);

        addBox.KeyDown += (s, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            string t = addBox.Text.Trim();
            if (t.Length == 0) return;
            if (dictating) { recording = false; return; } // Enter while listening = done
            var it = new Item { Title = t };
            addBox.Text = "";
            if (Cur.Checklist)
            {
                Cur.Items.Add(it); RenderItems(); scroll.ScrollToEnd();
            }
            else
            {
                Cur.Items.Insert(0, it); expanded = it; focusAfterKind = "details"; RenderItems(); scroll.ScrollToTop();
            }
            MarkDirty();
        };
        Grid.SetColumn(host, 1);
        addHost.Children.Add(host);
    }

    void RenderItems()
    {
        list.Children.Clear();
        expandedRun = null;
        var tab = Cur;
        if (tab.Drawing) { contentHost.Child = DrawCanvas(tab); return; }
        canvas = null;
        contentHost.Child = scroll;
        if (tab.Items.Count == 0)
            list.Children.Add(new TextBlock { Text = tab.Checklist ? "пока пусто — пиши дело внизу" : "пока пусто — пиши заметку внизу", Opacity = .35, FontSize = fs * .85, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        foreach (var it in tab.Items)
        {
            list.Children.Add(ItemRow(tab, it));
            if (it == expanded) list.Children.Add(Editor(tab, it));
        }
        if (focusAfter != null) { FocusLater(focusAfter); focusAfter = null; }
    }

    FrameworkElement MakeCheck(bool done, double size, Brush checkBr, double thick)
    {
        var c = new Canvas { Width = 20, Height = 20 };
        c.Children.Add(new SPath { Data = BoxGeo, Stroke = ink, StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round });
        if (done) c.Children.Add(new SPath { Data = CheckGeo, Stroke = checkBr ?? ink, StrokeThickness = thick + .9, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
        return new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = new Viewbox { Width = size, Height = size, Child = c } };
    }

    UIElement ItemRow(Tab tab, Item it)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tag = TagBrush(it);

        if (tab.Checklist)
        {
            var cb = MakeCheck(it.Done, fs * .8, cfg.TagStyle == "ink" ? tag : null, 1.7);
            cb.Margin = new Thickness(0, fs * .3, 8, 0);
            cb.VerticalAlignment = VerticalAlignment.Top;
            cb.MouseLeftButtonDown += (s, e) => { e.Handled = true; it.Done = !it.Done; MarkDirty(); RenderItems(); };
            row.Children.Add(cb);
        }

        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var run = new Span();
        AddInlines(run.Inlines, it.Title);
        if (tag != null)
        {
            if (cfg.TagStyle == "marker") run.Background = B(cfg.TagColors[it.Tag], 110);
            else if (cfg.TagStyle == "dot") tb.Inlines.Add(new Run("● ") { Foreground = tag, FontSize = fs * .7, BaselineAlignment = BaselineAlignment.Center });
            else tb.Foreground = tag;
        }
        if (!tab.Checklist) tb.FontWeight = FontWeights.Bold;
        tb.Inlines.Add(run);
        if (it.Done)
        {
            tb.Opacity = .5;
            tb.TextDecorations = new TextDecorationCollection { new TextDecoration(TextDecorationLocation.Strikethrough, new Pen(tag != null && cfg.TagStyle == "ink" ? tag : ink, 1.6), 0, TextDecorationUnit.FontRecommended, TextDecorationUnit.Pixel) };
        }
        if (it == expanded) expandedRun = run;

        FrameworkElement body = tb;
        if (!tab.Checklist || it.Details.Length > 0 || it.Subs.Count > 0)
        {
            var sp = new StackPanel();
            sp.Children.Add(tb);
            if (it != expanded)
            {
                string preview = Preview(it);
                if (preview.Length > 0)
                {
                    var pv = new TextBlock { FontSize = fs * .72, Opacity = .5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, -2, 0, 2) };
                    AddInlines(pv.Inlines, preview);
                    sp.Children.Add(pv);
                }
            }
            body = sp;
        }
        body.Cursor = Cursors.Hand;
        ((FrameworkElement)body).MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            expanded = expanded == it ? null : it;
            if (expanded != null && !tab.Checklist) focusAfterKind = "details";
            RenderItems();
        };
        Grid.SetColumn(body, 1);
        row.Children.Add(body);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, fs * .32, 0, 0) };
        var hover = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Hidden };
        var dot = new Ellipse { Width = fs * .48, Height = fs * .48, Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, ToolTip = "Цветная метка (правый клик — выбор)", VerticalAlignment = VerticalAlignment.Center };
        if (tag != null) dot.Fill = tag; else { dot.Stroke = ink; dot.StrokeThickness = 1.3; dot.StrokeDashArray = new DoubleCollection { 2, 2 }; dot.Fill = Brushes.Transparent; }
        dot.MouseLeftButtonDown += (s, e) => { e.Handled = true; it.Tag = it.Tag + 1 >= cfg.TagColors.Count ? -1 : it.Tag + 1; MarkDirty(); RenderItems(); };
        var x = new TextBlock { Text = "×", FontFamily = new FontFamily("Segoe UI"), FontSize = fs * .8, Opacity = .6, Cursor = Cursors.Hand, ToolTip = "Удалить (Ctrl+Z — вернуть)", Margin = new Thickness(0, -fs * .3, 0, 0) };
        x.MouseLeftButtonDown += (s, e) => { e.Handled = true; DeleteItem(tab, it); };
        hover.Children.Add(dot); hover.Children.Add(x);
        if (!tab.Checklist)
            tools.Children.Add(new TextBlock { Text = it.Created.ToString("d MMM", Ru), FontSize = fs * .62, Opacity = .4, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        tools.Children.Add(hover);
        Grid.SetColumn(tools, 2);
        row.Children.Add(tools);
        row.MouseEnter += delegate { hover.Visibility = Visibility.Visible; };
        row.MouseLeave += delegate { hover.Visibility = Visibility.Hidden; };
        row.ContextMenu = new ContextMenu(); // built only when actually opened
        row.ContextMenuOpening += delegate { row.ContextMenu = ItemMenu(tab, it); };
        return row;
    }

    static string Preview(Item it)
    {
        string d = (it.Details ?? "").Trim();
        if (d.Length > 0)
        {
            int nl = d.IndexOfAny(new[] { '\r', '\n' });
            if (nl > 0) d = d.Substring(0, nl);
            return d;
        }
        if (it.Subs.Count > 0) return it.Subs.Count(s => s.Done) + " из " + it.Subs.Count + " пунктов";
        return "";
    }

    void DeleteItem(Tab tab, Item it)
    {
        int idx = tab.Items.IndexOf(it);
        if (idx < 0) return;
        tab.Items.RemoveAt(idx);
        lastDeleted = Tuple.Create(tab, idx, it);
        if (expanded == it) expanded = null;
        MarkDirty(); RenderItems();
    }

    ContextMenu ItemMenu(Tab tab, Item it)
    {
        var m = new ContextMenu();
        for (int i = 0; i < cfg.TagColors.Count; i++)
        {
            int k = i;
            var mi = MI("Метка " + (i + 1), () => { it.Tag = k; MarkDirty(); RenderItems(); }, it.Tag == i);
            mi.Icon = new Ellipse { Width = 12, Height = 12, Fill = B(cfg.TagColors[i]) };
            m.Items.Add(mi);
        }
        m.Items.Add(MI("Без метки", () => { it.Tag = -1; MarkDirty(); RenderItems(); }, it.Tag < 0));
        m.Items.Add(new Separator());
        int idx = tab.Items.IndexOf(it);
        if (idx > 0) m.Items.Add(MI("Выше", () => { tab.Items.RemoveAt(idx); tab.Items.Insert(idx - 1, it); MarkDirty(); RenderItems(); }));
        if (idx < tab.Items.Count - 1) m.Items.Add(MI("Ниже", () => { tab.Items.RemoveAt(idx); tab.Items.Insert(idx + 1, it); MarkDirty(); RenderItems(); }));
        if (store.Tabs.Count > 1)
        {
            var mv = new MenuItem { Header = "Перенести во вкладку" };
            foreach (var t in store.Tabs.Where(t => t != tab))
            {
                var target = t;
                mv.Items.Add(MI(t.Name, () => { tab.Items.Remove(it); target.Items.Add(it); if (expanded == it) expanded = null; MarkDirty(); RenderItems(); }));
            }
            m.Items.Add(mv);
        }
        m.Items.Add(MI("Удалить", () => DeleteItem(tab, it)));
        return m;
    }

    UIElement Editor(Tab tab, Item it)
    {
        var box = new StackPanel();
        var border = new Border
        {
            BorderBrush = InkA(90), BorderThickness = new Thickness(1.5, 0, 0, 0), Padding = new Thickness(10, 2, 0, 4),
            Margin = new Thickness(tab.Checklist ? fs * .8 + 8 : 2, 2, 0, 8), Child = box
        };
        TextElement.SetFontSize(box, fs * .85);

        RichTextBox title = null, details = null;
        box.Children.Add(RichPlaceholder(title = RichBox(it.Title, fs * .9, false, FontWeights.Bold, md =>
        {
            if (PlainOf(md).Trim().Length == 0) return;
            it.Title = md;
            if (expandedRun != null) { expandedRun.Inlines.Clear(); AddInlines(expandedRun.Inlines, md); }
            MarkDirty();
        }), "название"));
        box.Children.Add(RichPlaceholder(details = RichBox(it.Details, fs * .85, true, FontWeights.Normal, md => { it.Details = md; MarkDirty(); }), "подробности…  (выдели текст — правый клик для оформления)"));
        title.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; FocusLater(details, true); } };

        var subs = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        for (int i = 0; i < it.Subs.Count; i++)
        {
            var sb = it.Subs[i];
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cb = MakeCheck(sb.Done, fs * .62, null, 1.8);
            cb.Margin = new Thickness(0, fs * .28, 7, 0); cb.VerticalAlignment = VerticalAlignment.Top;
            cb.MouseLeftButtonDown += (s, e) => { e.Handled = true; sb.Done = !sb.Done; MarkDirty(); RenderItems(); };
            g.Children.Add(cb);
            var t = RichBox(sb.Text, fs * .82, false, FontWeights.Normal, md => { sb.Text = md; MarkDirty(); });
            if (sb.Done)
            {
                t.Opacity = .5;
                foreach (var blk in t.Document.Blocks) blk.SetValue(Paragraph.TextDecorationsProperty, TextDecorations.Strikethrough);
            }
            t.LostKeyboardFocus += delegate { if (PlainOf(sb.Text).Trim().Length == 0 && it.Subs.Contains(sb)) { it.Subs.Remove(sb); MarkDirty(); Dispatcher.BeginInvoke(new Action(RenderItems)); } };
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            subs.Children.Add(g);
        }
        var addSub = HandBox("", fs * .82, false);
        addSub.KeyDown += (s, e) =>
        {
            if (e.Key != Key.Enter || addSub.Text.Trim().Length == 0) return;
            e.Handled = true;
            it.Subs.Add(new Sub { Text = addSub.Text.Trim() });
            focusAfterKind = "sub"; MarkDirty(); RenderItems();
        };
        var addRow = new Grid();
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.Children.Add(new TextBlock { Text = "+", Opacity = .45, FontSize = fs * .82, Margin = new Thickness(1, 0, 8, 0) });
        var addHostSub = Placeholder(addSub, "пункт");
        Grid.SetColumn(addHostSub, 1);
        addRow.Children.Add(addHostSub);
        subs.Children.Add(addRow);
        box.Children.Add(subs);

        box.Children.Add(new TextBlock { Text = "создано " + it.Created.ToString("d MMMM, HH:mm", Ru), FontSize = fs * .58, Opacity = .38, Margin = new Thickness(0, 4, 0, 0) });

        if (focusAfterKind == "details") FocusLater(details, true);
        else if (focusAfterKind == "sub") FocusLater(addSub);
        focusAfterKind = null;
        return border;
    }

    TextBox HandBox(string text, double size, bool multiline)
    {
        var t = new TextBox
        {
            Text = text, FontSize = size, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Foreground = ink, CaretBrush = ink, SelectionBrush = ink, SelectionOpacity = .3, FocusVisualStyle = null,
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = multiline, AcceptsTab = false
        };
        t.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            int idx = t.GetCharacterIndexFromPoint(e.GetPosition(t), true);
            string url = UrlAt(t.Text, idx);
            if (url != null) { e.Handled = true; OpenUrl(url); }
        };
        return t;
    }

    // ---------------- links ----------------
    static readonly Regex UrlRe = new Regex(@"(?:https?://|www\.)[^\s<>""']+", RegexOptions.IgnoreCase);

    static string CleanUrl(string u) { return u.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '»', '"', '\''); }

    static string UrlAt(string text, int idx)
    {
        if (text == null || idx < 0) return null;
        foreach (Match m in UrlRe.Matches(text))
        {
            string u = CleanUrl(m.Value);
            if (idx >= m.Index && idx <= m.Index + u.Length) return u;
        }
        return null;
    }

    static void OpenUrl(string url)
    {
        if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    Brush LinkBrush { get { return B(cfg.TagColors.Count > 3 ? cfg.TagColors[3] : "#4A90FF"); } }

    // ---------------- formatting: a tiny markdown kept in data.json ----------------
    // **bold**  *italic*  ~~strike~~  __underline__  ==marker==
    const int FB = 1, FI = 2, FS = 4, FU = 8, FH = 16;
    static readonly string[] Marks = { "**", "~~", "__", "==", "*" };
    static readonly int[] MarkBits = { FB, FS, FU, FH, FI };

    static List<Tuple<string, int>> ParseMd(string s)
    {
        var res = new List<Tuple<string, int>>();
        var cur = new StringBuilder();
        int flags = 0, i = 0;
        s = s ?? "";
        while (i < s.Length)
        {
            int k = -1;
            for (int j = 0; j < Marks.Length; j++)
                if (string.CompareOrdinal(s, i, Marks[j], 0, Marks[j].Length) == 0) { k = j; break; }
            if (k >= 0)
            {
                string m = Marks[k];
                bool on = (flags & MarkBits[k]) != 0, ok;
                if (!on) ok = i + m.Length < s.Length && !char.IsWhiteSpace(s[i + m.Length]) && s.IndexOf(m, i + m.Length + 1, StringComparison.Ordinal) > 0;
                else ok = i > 0 && !char.IsWhiteSpace(s[i - 1]);
                if (ok)
                {
                    if (cur.Length > 0) { res.Add(Tuple.Create(cur.ToString(), flags)); cur.Clear(); }
                    flags ^= MarkBits[k];
                    i += m.Length;
                    continue;
                }
            }
            cur.Append(s[i]); i++;
        }
        if (cur.Length > 0) res.Add(Tuple.Create(cur.ToString(), flags));
        return res;
    }

    static string PlainOf(string md) { return string.Concat(ParseMd(md).Select(x => x.Item1)); }

    static string ToMd(List<Tuple<string, int>> segs)
    {
        var merged = new List<Tuple<string, int>>();
        foreach (var sg in segs)
        {
            if (sg.Item1.Length == 0) continue;
            if (merged.Count > 0 && merged[merged.Count - 1].Item2 == sg.Item2)
                merged[merged.Count - 1] = Tuple.Create(merged[merged.Count - 1].Item1 + sg.Item1, sg.Item2);
            else merged.Add(sg);
        }
        var order = new[] { FH, FB, FI, FU, FS };
        var mark = new Dictionary<int, string> { { FH, "==" }, { FB, "**" }, { FI, "*" }, { FU, "__" }, { FS, "~~" } };
        var sb = new StringBuilder();
        foreach (var sg in merged)
        {
            // markers must hug the text, so leading/trailing spaces stay outside them
            string t = sg.Item1;
            if (sg.Item2 == 0 || t.Trim().Length == 0) { sb.Append(t); continue; }
            foreach (var line in t.Split('\n').Select((v, n) => new { v, n }))
            {
                if (line.n > 0) sb.Append('\n');
                string core = line.v.Trim();
                if (core.Length == 0) { sb.Append(line.v); continue; }
                int lead = line.v.Length - line.v.TrimStart().Length;
                sb.Append(line.v.Substring(0, lead));
                foreach (var b in order) if ((sg.Item2 & b) != 0) sb.Append(mark[b]);
                sb.Append(core);
                foreach (var b in order.Reverse()) if ((sg.Item2 & b) != 0) sb.Append(mark[b]);
                sb.Append(line.v.Substring(lead + core.Length));
            }
        }
        return sb.ToString();
    }

    void StyleInline(TextElement el, int f)
    {
        if ((f & FB) != 0) el.FontWeight = FontWeights.Bold;
        if ((f & FI) != 0) el.FontStyle = FontStyles.Italic;
        var inl = el as Inline;
        if (inl != null && (f & (FS | FU)) != 0)
        {
            var d = new TextDecorationCollection();
            if ((f & FS) != 0) d.Add(TextDecorations.Strikethrough);
            if ((f & FU) != 0) d.Add(TextDecorations.Underline);
            inl.TextDecorations = d;
        }
        if ((f & FH) != 0) el.Background = B(cfg.TagColors.Count > 1 ? cfg.TagColors[1] : "#FFB020", 105);
    }

    // named links are stored as [название](https://...), bare addresses stay as they are
    static readonly Regex NamedLinkRe = new Regex(@"\[([^\]\n]+)\]\(((?:https?://|www\.)[^)\s]+)\)", RegexOptions.IgnoreCase);

    // (index, length, label or null for a bare address, url) — in order, never overlapping
    static List<Tuple<int, int, string, string>> FindLinks(string text)
    {
        var res = new List<Tuple<int, int, string, string>>();
        int pos = 0;
        foreach (Match nm in NamedLinkRe.Matches(text))
        {
            AddBare(text, pos, nm.Index, res);
            res.Add(Tuple.Create(nm.Index, nm.Length, nm.Groups[1].Value, nm.Groups[2].Value));
            pos = nm.Index + nm.Length;
        }
        AddBare(text, pos, text.Length, res);
        return res;
    }

    static void AddBare(string text, int from, int to, List<Tuple<int, int, string, string>> res)
    {
        if (to <= from) return;
        foreach (Match m in UrlRe.Matches(text.Substring(from, to - from)))
        {
            string u = CleanUrl(m.Value);
            res.Add(Tuple.Create(from + m.Index, u.Length, (string)null, u));
        }
    }

    static Uri ToUri(string url)
    {
        if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        Uri u;
        return Uri.TryCreate(url, UriKind.Absolute, out u) ? u : null;
    }

    // read-only display (list rows, previews): formatting + Ctrl+click links
    void AddInlines(InlineCollection target, string md)
    {
        foreach (var sg in ParseMd(md))
        {
            string text = sg.Item1;
            int pos = 0;
            foreach (var l in FindLinks(text))
            {
                if (l.Item1 > pos) AddText(target, text.Substring(pos, l.Item1 - pos), sg.Item2);
                var link = new Hyperlink(new Run(l.Item3 ?? l.Item4)) { Foreground = LinkBrush, Cursor = Cursors.Hand,
                    ToolTip = (l.Item3 != null ? l.Item4 + "\n" : "") + "Ctrl+клик — открыть" };
                StyleInline(link, sg.Item2);
                string url = l.Item4;
                link.PreviewMouseLeftButtonDown += (s, e) => { if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; OpenUrl(url); } };
                target.Add(link);
                pos = l.Item1 + l.Item2;
            }
            if (pos < text.Length) AddText(target, text.Substring(pos), sg.Item2);
        }
    }

    Hyperlink EditorLink(string label, string url, int flags)
    {
        var run = new Run(label);
        StyleInline(run, flags);
        var h = new Hyperlink(run) { Foreground = LinkBrush, ToolTip = url + "\nCtrl+клик — открыть, правый клик — показать или переименовать" };
        h.NavigateUri = ToUri(url);
        return h;
    }

    void AddText(InlineCollection target, string text, int f)
    {
        var parts = text.Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) target.Add(new LineBreak());
            if (parts[i].Length == 0) continue;
            var r = new Run(parts[i]);
            StyleInline(r, f);
            target.Add(r);
        }
    }

    // editable rich text box that saves itself back as the tiny markdown
    RichTextBox RichBox(string md, double size, bool multiline, FontWeight baseWeight, Action<string> changed)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontFamily = font, FontSize = size, Foreground = ink, FontWeight = baseWeight };
        var para = new Paragraph { Margin = new Thickness(0) };
        doc.Blocks.Add(para);
        foreach (var sg in ParseMd(md))
        {
            var lines = sg.Item1.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) { para = new Paragraph { Margin = new Thickness(0) }; doc.Blocks.Add(para); }
                if (lines[i].Length == 0) continue;
                int pos = 0;
                foreach (var l in FindLinks(lines[i]))
                {
                    if (l.Item1 > pos) { var r0 = new Run(lines[i].Substring(pos, l.Item1 - pos)); StyleInline(r0, sg.Item2); para.Inlines.Add(r0); }
                    if (l.Item3 != null && ToUri(l.Item4) != null) para.Inlines.Add(EditorLink(l.Item3, l.Item4, sg.Item2));
                    else { var lr = new Run(lines[i].Substring(l.Item1, l.Item2)) { Foreground = LinkBrush }; StyleInline(lr, sg.Item2); para.Inlines.Add(lr); }
                    pos = l.Item1 + l.Item2;
                }
                if (pos < lines[i].Length) { var r = new Run(lines[i].Substring(pos)); StyleInline(r, sg.Item2); para.Inlines.Add(r); }
            }
        }
        var rtb = new RichTextBox
        {
            Document = doc, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Foreground = ink, CaretBrush = ink, SelectionBrush = ink, SelectionOpacity = .3, FocusVisualStyle = null,
            AcceptsReturn = multiline, AcceptsTab = false, FontFamily = font, FontSize = size, FontWeight = baseWeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        bool loading = true;
        rtb.TextChanged += delegate { if (!loading) changed(RichToMd(rtb, baseWeight)); };
        loading = false;

        // paste as plain text so the handwriting look survives
        DataObject.AddPastingHandler(rtb, (s, e) =>
        {
            if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
            string txt = (string)e.DataObject.GetData(DataFormats.UnicodeText);
            if (!multiline) txt = txt.Replace("\r", " ").Replace("\n", " ");
            var plain = new DataObject();
            plain.SetData(DataFormats.UnicodeText, txt);
            e.DataObject = plain;
        });

        rtb.PreviewKeyDown += (s, e) =>
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (ctrl && shift && e.Key == Key.X) { ToggleDecoration(rtb, TextDecorationLocation.Strikethrough); e.Handled = true; }
            else if (ctrl && shift && e.Key == Key.H) { ToggleMarker(rtb); e.Handled = true; }
            else if (ctrl && (e.Key == Key.E || e.Key == Key.L || e.Key == Key.R || e.Key == Key.J || e.Key == Key.OemOpenBrackets || e.Key == Key.OemCloseBrackets)) e.Handled = true; // no alignment / font-size tricks
        };
        Func<Point, string> urlUnder = pt =>
        {
            var tp = rtb.GetPositionFromPoint(pt, false);
            if (tp == null || tp.Paragraph == null) return null;
            var hl = LinkAt(tp);
            if (hl != null) return hl.NavigateUri != null ? hl.NavigateUri.OriginalString : null;
            string text = new TextRange(tp.Paragraph.ContentStart, tp.Paragraph.ContentEnd).Text;
            return UrlAt(text, new TextRange(tp.Paragraph.ContentStart, tp).Text.Length);
        };
        rtb.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            string url = urlUnder(e.GetPosition(rtb));
            if (url != null) { e.Handled = true; OpenUrl(url); }
        };
        // hand cursor over a link while Ctrl is held, so it's clear the link can be opened
        rtb.PreviewMouseMove += (s, e) =>
        {
            bool hand = (Keyboard.Modifiers & ModifierKeys.Control) != 0 && urlUnder(e.GetPosition(rtb)) != null;
            rtb.Cursor = hand ? Cursors.Hand : null;
        };
        rtb.ContextMenu = new ContextMenu();
        rtb.ContextMenuOpening += (s, e) => { rtb.ContextMenu = FormatMenu(rtb); };
        return rtb;
    }

    string RichToMd(RichTextBox rtb, FontWeight baseWeight)
    {
        var segs = new List<Tuple<string, int>>();
        bool first = true;
        foreach (var block in rtb.Document.Blocks)
        {
            if (!first) segs.Add(Tuple.Create("\n", 0));
            first = false;
            var p = block as Paragraph;
            if (p == null) { segs.Add(Tuple.Create(new TextRange(block.ContentStart, block.ContentEnd).Text, 0)); continue; }
            Collect(p.Inlines, segs, baseWeight);
        }
        return ToMd(segs).TrimEnd('\n');
    }

    static void Collect(InlineCollection inlines, List<Tuple<string, int>> segs, FontWeight baseWeight)
    {
        foreach (var x in inlines)
        {
            if (x is LineBreak) { segs.Add(Tuple.Create("\n", 0)); continue; }
            var hl = x as Hyperlink;
            if (hl != null)
            {
                string label = new TextRange(hl.ContentStart, hl.ContentEnd).Text.Replace("[", "(").Replace("]", ")");
                var firstRun = hl.Inlines.OfType<Run>().FirstOrDefault();
                if (label.Trim().Length == 0) continue; // the name was erased -> drop the link
                if (hl.NavigateUri == null)
                {
                    // never save a link without its address: fall back to the address kept in the tooltip
                    string tip = hl.ToolTip as string;
                    string keep = tip != null ? tip.Split('\n')[0] : null;
                    segs.Add(Tuple.Create(keep != null && ToUri(keep) != null ? "[" + label + "](" + keep + ")" : label, firstRun != null ? RunFlags(firstRun, baseWeight) : 0));
                    continue;
                }
                segs.Add(Tuple.Create("[" + label + "](" + hl.NavigateUri.OriginalString + ")", firstRun != null ? RunFlags(firstRun, baseWeight) : 0));
                continue;
            }
            var sp = x as Span;
            if (sp != null) { Collect(sp.Inlines, segs, baseWeight); continue; }
            var r = x as Run;
            if (r == null) continue;
            segs.Add(Tuple.Create(r.Text, RunFlags(r, baseWeight)));
        }
    }

    static int RunFlags(Run r, FontWeight baseWeight)
    {
        {
            int f = 0;
            if (r.FontWeight.ToOpenTypeWeight() >= 600 && baseWeight.ToOpenTypeWeight() < 600) f |= FB;
            if (r.FontStyle == FontStyles.Italic) f |= FI;
            for (DependencyObject d = r; d is Inline; d = ((Inline)d).Parent)
            {
                var inl = (Inline)d;
                if (inl is Hyperlink) continue; // the link's own underline isn't user formatting
                if (inl.TextDecorations != null)
                    foreach (var td in inl.TextDecorations)
                    {
                        if (td.Location == TextDecorationLocation.Strikethrough) f |= FS;
                        if (td.Location == TextDecorationLocation.Underline) f |= FU;
                    }
                if (inl.Background != null) f |= FH;
            }
            return f;
        }
    }

    static Hyperlink LinkAt(TextPointer tp)
    {
        for (DependencyObject d = tp == null ? null : tp.Parent; d is TextElement; d = ((TextElement)d).Parent)
            if (d is Hyperlink) return (Hyperlink)d;
        return null;
    }

    // TextPointer for the n-th visible character of a paragraph (offsets in TextPointer count element tags too)
    static TextPointer At(Paragraph p, int n)
    {
        var tp = p.ContentStart;
        while (tp != null && tp.CompareTo(p.ContentEnd) < 0)
        {
            if (tp.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                int len = tp.GetTextRunLength(LogicalDirection.Forward);
                if (n <= len) return tp.GetPositionAtOffset(n);
                n -= len;
                tp = tp.GetPositionAtOffset(len);
            }
            else tp = tp.GetNextContextPosition(LogicalDirection.Forward);
        }
        return p.ContentEnd;
    }

    // the bare address the user selected, or the one the caret stands in
    static TextRange BareUrlRange(RichTextBox rtb)
    {
        var sel = rtb.Selection;
        if (LinkAt(sel.Start) != null) return null;
        var p = sel.Start.Paragraph;
        if (p == null) return null;
        string text = new TextRange(p.ContentStart, p.ContentEnd).Text;
        int idx = new TextRange(p.ContentStart, sel.Start).Text.Length;
        foreach (var l in FindLinks(text))
            if (l.Item3 == null && idx >= l.Item1 && idx <= l.Item1 + l.Item2)
                return new TextRange(At(p, l.Item1), At(p, l.Item1 + l.Item2));
        return null;
    }

    void HideLink(RichTextBox rtb, TextRange range)
    {
        string url = range.Text.Trim();
        AskText(rtb, "Как подписать ссылку?", "", label =>
        {
            range.Text = "";
            InsertLink(range.Start, label, url);
        });
    }

    // The link is built completely (address included) BEFORE it goes into the document:
    // the editor saves on every change, and a link inserted first and given its address
    // afterwards was saved without the address.
    void InsertLink(TextPointer at, string label, string url)
    {
        var h = EditorLink(label, url, 0);
        if (h.NavigateUri == null) { new Run(label + " " + url, at); return; }
        new Span(h, at);
    }

    void AskText(RichTextBox rtb, string prompt, string initial, Action<string> done)
    {
        var tb = HandBox(initial, fs * .8, false);
        tb.Tag = "popup"; tb.MinWidth = 200;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = prompt + "  (Enter — готово, Esc — отмена)", FontSize = fs * .6, Opacity = .6, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(tb);
        var border = new Border { Background = paperBr, BorderBrush = InkA(120), BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 8), CornerRadius = new CornerRadius(4), Child = panel };
        TextElement.SetFontFamily(border, font);
        TextElement.SetForeground(border, ink);
        var rect = rtb.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
        var pop = new Popup { Child = border, PlacementTarget = rtb, Placement = PlacementMode.Bottom, PlacementRectangle = rect, StaysOpen = false, AllowsTransparency = true };
        pop.Opened += delegate { HideFromCapture(border); };
        tb.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter && tb.Text.Trim().Length > 0) { e.Handled = true; pop.IsOpen = false; done(tb.Text.Trim()); rtb.Focus(); }
            else if (e.Key == Key.Escape) { e.Handled = true; pop.IsOpen = false; rtb.Focus(); }
        };
        pop.IsOpen = true;
        FocusLater(tb);
    }

    // context menus, tooltips and popups are separate little windows: keep them off streams too
    void HideFromCapture(Visual v)
    {
        var src = PresentationSource.FromVisual(v) as HwndSource;
        if (src != null && cfg.HideFromCapture) SetWindowDisplayAffinity(src.Handle, 0x11);
    }

    static void ToggleDecoration(RichTextBox rtb, TextDecorationLocation loc)
    {
        var sel = rtb.Selection;
        if (sel.IsEmpty) return;
        var cur = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        bool has = cur != null && cur.Any(d => d.Location == loc);
        var next = new TextDecorationCollection();
        if (cur != null) foreach (var d in cur) if (d.Location != loc) next.Add(d);
        if (!has) next.Add(loc == TextDecorationLocation.Strikethrough ? TextDecorations.Strikethrough[0] : TextDecorations.Underline[0]);
        sel.ApplyPropertyValue(Inline.TextDecorationsProperty, next);
    }

    void ToggleMarker(RichTextBox rtb)
    {
        var sel = rtb.Selection;
        if (sel.IsEmpty) return;
        bool has = sel.GetPropertyValue(TextElement.BackgroundProperty) is Brush;
        sel.ApplyPropertyValue(TextElement.BackgroundProperty, has ? null : B(cfg.TagColors.Count > 1 ? cfg.TagColors[1] : "#FFB020", 105));
    }

    ContextMenu FormatMenu(RichTextBox rtb)
    {
        var m = new ContextMenu();
        bool sel = !rtb.Selection.IsEmpty;
        Func<string, string, Action, MenuItem> item = (h, g, a) =>
        {
            var mi = new MenuItem { Header = h, InputGestureText = g, IsEnabled = sel };
            mi.Click += delegate { a(); rtb.Focus(); };
            return mi;
        };
        m.Items.Add(item("Жирный", "Ctrl+B", () => EditingCommands.ToggleBold.Execute(null, rtb)));
        m.Items.Add(item("Курсив", "Ctrl+I", () => EditingCommands.ToggleItalic.Execute(null, rtb)));
        m.Items.Add(item("Подчеркнуть", "Ctrl+U", () => ToggleDecoration(rtb, TextDecorationLocation.Underline)));
        m.Items.Add(item("Зачеркнуть", "Ctrl+Shift+X", () => ToggleDecoration(rtb, TextDecorationLocation.Strikethrough)));
        m.Items.Add(item("Маркер", "Ctrl+Shift+H", () => ToggleMarker(rtb)));
        m.Items.Add(item("Убрать оформление", "", () =>
        {
            var s = rtb.Selection;
            s.ApplyPropertyValue(TextElement.FontWeightProperty, rtb.FontWeight);
            s.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
            s.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
            s.ApplyPropertyValue(TextElement.BackgroundProperty, null);
        }));
        m.Items.Add(new Separator());
        m.Items.Add(new MenuItem { Header = "Вырезать", Command = ApplicationCommands.Cut, CommandTarget = rtb });
        m.Items.Add(new MenuItem { Header = "Копировать", Command = ApplicationCommands.Copy, CommandTarget = rtb });
        m.Items.Add(new MenuItem { Header = "Вставить", Command = ApplicationCommands.Paste, CommandTarget = rtb });
        Func<string, Action, MenuItem> act = (h, a) => { var mi = new MenuItem { Header = h }; mi.Click += delegate { a(); }; return mi; };
        var hl = LinkAt(rtb.Selection.Start);
        var bare = BareUrlRange(rtb);
        if (hl != null && hl.NavigateUri != null)
        {
            string url = hl.NavigateUri.OriginalString;
            m.Items.Add(new Separator());
            m.Items.Add(act("Открыть ссылку   (Ctrl+клик)", () => OpenUrl(url)));
            m.Items.Add(act("Переименовать ссылку…", () => AskText(rtb, "Новая подпись ссылки", new TextRange(hl.ContentStart, hl.ContentEnd).Text, label =>
            {
                hl.Inlines.Clear();
                hl.Inlines.Add(new Run(label));
            })));
            m.Items.Add(act("Показать адрес целиком", () => { new TextRange(hl.ElementStart, hl.ElementEnd).Text = url; }));
            m.Items.Add(act("Копировать адрес", () => { try { Clipboard.SetText(url); } catch { } }));
        }
        else if (bare != null)
        {
            string url = bare.Text.Trim();
            m.Items.Add(new Separator());
            m.Items.Add(act("Открыть ссылку   (Ctrl+клик)", () => OpenUrl(url)));
            m.Items.Add(act("Спрятать ссылку под текст…", () => HideLink(rtb, bare)));
        }
        else if (sel)
        {
            string clip = null;
            try { if (Clipboard.ContainsText()) clip = Clipboard.GetText().Trim(); } catch { }
            if (clip != null && clip.IndexOfAny(new[] { ' ', '\n' }) < 0 && ToUri(clip) != null && UrlRe.IsMatch(clip))
            {
                var range = new TextRange(rtb.Selection.Start, rtb.Selection.End);
                string url = clip;
                m.Items.Add(new Separator());
                m.Items.Add(act("Сделать ссылкой (адрес из буфера)", () =>
                {
                    string label = range.Text.Trim();
                    range.Text = "";
                    InsertLink(range.Start, label, url);
                }));
            }
        }
        return m;
    }

    Grid RichPlaceholder(RichTextBox r, string hint)
    {
        var g = new Grid();
        var ph = new TextBlock { Text = hint, FontSize = r.FontSize, Opacity = .4, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Func<bool> empty = () => new TextRange(r.Document.ContentStart, r.Document.ContentEnd).Text.Trim().Length == 0;
        ph.Visibility = empty() ? Visibility.Visible : Visibility.Collapsed;
        r.TextChanged += delegate { ph.Visibility = empty() ? Visibility.Visible : Visibility.Collapsed; };
        g.Children.Add(ph);
        g.Children.Add(r);
        return g;
    }

    Grid Placeholder(TextBox t, string hint)
    {
        var g = new Grid { Tag = t };
        var ph = new TextBlock { Text = hint, FontSize = t.FontSize, Opacity = .4, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0) };
        ph.Visibility = t.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        t.TextChanged += delegate { ph.Visibility = t.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
        g.Children.Add(ph);
        g.Children.Add(t);
        return g;
    }

    void FocusLater(Control c, bool toEnd = false)
    {
        if (c == null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            c.Focus(); Keyboard.Focus(c);
            var t = c as TextBox;
            if (t != null) { if (toEnd) t.CaretIndex = t.Text.Length; else t.SelectAll(); }
            var r = c as RichTextBox;
            if (r != null) { if (toEnd) r.CaretPosition = r.Document.ContentEnd; else r.SelectAll(); }
        }));
    }

    // ---------------- dictation: offline speech recognition (Vosk) ----------------
    // Engine + Russian model live in %APPDATA%\DeskNotes\vosk\{lib,model}. Nothing runs until the mic is pressed;
    // the recording thread exists only while listening and the model is freed 3 minutes after the last use.
    const string VOSK = "libvosk.dll";
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string p);
    [DllImport(VOSK, CallingConvention = CallingConvention.Cdecl)] static extern void vosk_set_log_level(int level);
    [DllImport(VOSK, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr vosk_model_new(byte[] path);
    [DllImport(VOSK, CallingConvention = CallingConvention.Cdecl)] static extern void vosk_model_free(IntPtr m);
    [DllImport(VOSK, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr vosk_recognizer_new(IntPtr m, float rate);
    [DllImport(VOSK, CallingConvention = CallingConvention.Cdecl)] static extern int vosk_recognizer_accept_waveform(IntPtr r, byte[] data, int len);
    [DllImport(VOSK, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr vosk_recognizer_result(IntPtr r);
    [DllImport(VOSK, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr vosk_recognizer_partial_result(IntPtr r);
    [DllImport(VOSK, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr vosk_recognizer_final_result(IntPtr r);
    [DllImport(VOSK, CallingConvention = CallingConvention.Cdecl)] static extern void vosk_recognizer_free(IntPtr r);

    [StructLayout(LayoutKind.Sequential)] struct WaveFormat { public ushort tag, channels; public uint rate, bytesPerSec; public ushort blockAlign, bits, cb; }
    [StructLayout(LayoutKind.Sequential)] struct WaveHdr { public IntPtr data; public uint len, recorded; public IntPtr user; public uint flags, loops; public IntPtr next, reserved; }
    [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, uint dev, ref WaveFormat fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);

    bool dictating;
    volatile bool recording;
    IntPtr voskModel;
    readonly object voskLock = new object();
    DispatcherTimer modelIdle;
    static string VoskDir { get { return F("vosk"); } }

    void StartDictation()
    {
        if (recording) { recording = false; return; } // second press = done; the thread posts the result
        if (dictating) return;                         // still finishing the previous phrase
        if (!IOFile.Exists(IOPath.Combine(VoskDir, "lib", VOSK)) || !IODir.Exists(IOPath.Combine(VoskDir, "model")))
        {
            MessageBox.Show("Распознавание речи не установлено: нет папки " + VoskDir, "DeskNotes");
            return;
        }
        if (Cur.Drawing) SwitchTab(store.Tabs.FindIndex(t => !t.Drawing));
        dictating = true; recording = true;
        if (modelIdle != null) modelIdle.Stop();
        RenderAdd();
        FocusLater(addBox, true);
        new Thread(RecordLoop) { IsBackground = true, Name = "dictation" }.Start();
    }

    void UI(Action a) { Dispatcher.BeginInvoke(a); }

    static string Utf8(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        int n = 0;
        while (Marshal.ReadByte(p, n) != 0) n++;
        var b = new byte[n];
        Marshal.Copy(p, b, 0, n);
        return Encoding.UTF8.GetString(b);
    }

    static string JsonField(string json, string key)
    {
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Regex.Unescape(m.Groups[1].Value).Trim() : "";
    }

    void RecordLoop()
    {
        string error = null;
        var done = new StringBuilder();
        IntPtr rec = IntPtr.Zero, h = IntPtr.Zero;
        var hdrs = new List<IntPtr>();
        int hsz = Marshal.SizeOf(typeof(WaveHdr));
        int flagsOff = (int)Marshal.OffsetOf(typeof(WaveHdr), "flags");
        int recOff = (int)Marshal.OffsetOf(typeof(WaveHdr), "recorded");
        try
        {
            lock (voskLock)
            {
                if (voskModel == IntPtr.Zero)
                {
                    SetDllDirectory(IOPath.Combine(VoskDir, "lib"));
                    vosk_set_log_level(-1);
                    voskModel = vosk_model_new(Encoding.UTF8.GetBytes(IOPath.Combine(VoskDir, "model") + "\0"));
                }
                if (voskModel == IntPtr.Zero) throw new Exception("не загрузилась модель распознавания");
                rec = vosk_recognizer_new(voskModel, 16000f);
            }
            var fmt = new WaveFormat { tag = 1, channels = 1, rate = 16000, bytesPerSec = 32000, blockAlign = 2, bits = 16 };
            int mr = waveInOpen(out h, 0xFFFFFFFF, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (mr != 0) { h = IntPtr.Zero; throw new Exception("не открылся микрофон (код " + mr + "). Проверь, что он подключён и разрешён в Параметры → Конфиденциальность → Микрофон"); }
            for (int i = 0; i < 4; i++)
            {
                var hp = Marshal.AllocHGlobal(hsz);
                Marshal.StructureToPtr(new WaveHdr { data = Marshal.AllocHGlobal(3200), len = 3200 }, hp, false);
                hdrs.Add(hp);
                waveInPrepareHeader(h, hp, hsz);
                waveInAddBuffer(h, hp, hsz);
            }
            UI(() => ShowHeard("", true));
            waveInStart(h);

            var chunk = new byte[3200];
            DateTime started = DateTime.Now, lastChange = DateTime.Now;
            string partial = "";
            while (recording)
            {
                bool any = false;
                foreach (var hp in hdrs)
                {
                    if ((Marshal.ReadInt32(hp, flagsOff) & 1) == 0) continue; // WHDR_DONE
                    any = true;
                    int n = Marshal.ReadInt32(hp, recOff);
                    Marshal.Copy(Marshal.ReadIntPtr(hp), chunk, 0, n);
                    if (vosk_recognizer_accept_waveform(rec, chunk, n) != 0)
                    {
                        string t = JsonField(Utf8(vosk_recognizer_result(rec)), "text");
                        if (t.Length > 0) { if (done.Length > 0) done.Append(". "); done.Append(t); lastChange = DateTime.Now; }
                        partial = "";
                    }
                    else
                    {
                        string p = JsonField(Utf8(vosk_recognizer_partial_result(rec)), "partial");
                        if (p != partial) { partial = p; lastChange = DateTime.Now; }
                    }
                    string show = done.ToString() + (partial.Length > 0 ? (done.Length > 0 ? ". " : "") + partial : "");
                    UI(() => ShowHeard(show, false));
                    Marshal.WriteInt32(hp, flagsOff, 2); // keep WHDR_PREPARED, clear DONE
                    Marshal.WriteInt32(hp, recOff, 0);
                    waveInAddBuffer(h, hp, hsz);
                }
                bool spoke = done.Length > 0 || partial.Length > 0;
                double since = (DateTime.Now - lastChange).TotalSeconds, total = (DateTime.Now - started).TotalSeconds;
                if (spoke && since > 3) break;      // 3 s pause after speaking = finished
                if (!spoke && total > 15) break;    // nothing said
                if (total > 180) break;
                if (!any) Thread.Sleep(30);
            }
            string fin = JsonField(Utf8(vosk_recognizer_final_result(rec)), "text");
            if (fin.Length > 0) { if (done.Length > 0) done.Append(". "); done.Append(fin); }
        }
        catch (DllNotFoundException) { error = "не найден движок распознавания в " + VoskDir; }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            if (h != IntPtr.Zero)
            {
                waveInReset(h);
                foreach (var hp in hdrs) waveInUnprepareHeader(h, hp, hsz);
                waveInClose(h);
            }
            foreach (var hp in hdrs) { Marshal.FreeHGlobal(Marshal.ReadIntPtr(hp)); Marshal.FreeHGlobal(hp); }
            if (rec != IntPtr.Zero) vosk_recognizer_free(rec);
        }
        string text = done.ToString();
        UI(() => FinishDictation(text, error));
    }

    void ShowHeard(string text, bool ready)
    {
        if (!dictating || addBox == null) return;
        addBox.Text = text;
        addBox.CaretIndex = text.Length;
    }

    void FinishDictation(string text, string error)
    {
        recording = false; dictating = false;
        RenderAdd();
        if (error != null) MessageBox.Show("Голосовой ввод не сработал: " + error, "DeskNotes");
        else if (text.Trim().Length > 0) HandleDictation(text);
        FocusLater(addBox);
        if (modelIdle == null)
        {
            modelIdle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) }; // the model takes ~400 MB: give it back quickly
            modelIdle.Tick += delegate
            {
                modelIdle.Stop();
                lock (voskLock) { if (!recording && voskModel != IntPtr.Zero) { vosk_model_free(voskModel); voskModel = IntPtr.Zero; } }
                TrimMemory();
            };
        }
        modelIdle.Stop(); modelIdle.Start();
    }

    static readonly string[] NotePrefixes = { "запиши в заметки", "запиши заметку", "добавь заметку", "запомни", "заметка", "заметку" };

    void HandleDictation(string text)
    {
        string low = text.ToLower(Ru);
        bool asNote = !Cur.Checklist;
        foreach (var p in NotePrefixes)
            if (low.StartsWith(p)) { asNote = true; text = text.Substring(p.Length).TrimStart(' ', ',', ':', '.', '-', '—'); break; }
        if (text.Trim().Length == 0) return;

        if (asNote)
        {
            int ti = Cur.Checklist ? store.Tabs.FindIndex(t => !t.Checklist && !t.Drawing) : store.Active;
            if (ti < 0) { store.Tabs.Add(new Tab { Name = "Заметки", Checklist = false }); ti = store.Tabs.Count - 1; }
            var tab = store.Tabs[ti];
            string first = Regex.Split(text.Trim(), @"(?<=[.!?])\s+")[0].TrimEnd('.', '!', '?', ',');
            if (first.Length > 48) first = first.Substring(0, 48).TrimEnd() + "…";
            var it = new Item { Title = Cap(first), Details = Cap(text.Trim()) };
            tab.Items.Insert(0, it);
            if (ti != store.Active) { store.Active = ti; RebuildAll(); }
            expanded = it;
            MarkDirty(); RenderItems(); scroll.ScrollToTop();
            return;
        }

        var parts = SplitTasks(text);
        foreach (var p in parts) Cur.Items.Add(new Item { Title = p });
        MarkDirty(); RenderItems(); scroll.ScrollToEnd();
    }

    const string Joiners = @"потом|затем|после этого|ещё|еще|также|а также|и ещё|и еще|кроме того|во-первых|во-вторых|в-третьих|в-четв[её]ртых|в-пятых";
    const string Fillers = @"и|а|но|так|итак|ну|короче|потом|затем|после этого|ещё|еще|также|а также|кроме того|во-первых|во-вторых|в-третьих|в-четв[её]ртых|в-пятых|у меня|сегодня|завтра мне|мне|надо|нужно|необходимо|я хочу|хочу|я должен|должен|не забыть|не забудь";

    // "Сегодня надо купить хлеб. Потом позвонить маме, и ещё доделать курсовую" -> 3 tasks.
    // Commas alone don't split, so "купить хлеб, молоко, яйца" stays one task.
    static List<string> SplitTasks(string text)
    {
        var res = new List<string>();
        var pieces = Regex.Split(text, @"(?<=[.!?;])\s+|\s*;\s*|,?\s+(?=(?:" + Joiners + @")(?:\s|,|$))", RegexOptions.IgnoreCase);
        var filler = new Regex(@"^(?:(?:" + Fillers + @")(?:\s+|,\s*|:\s*))+", RegexOptions.IgnoreCase);
        foreach (var raw in pieces)
        {
            string p = raw.Trim().Trim(',', ' ');
            for (int i = 0; i < 4; i++) { string q = filler.Replace(p, ""); if (q == p) break; p = q; }
            p = p.Trim().TrimEnd('.', '!', ';', ',', ' ');
            if (p.Length >= 2) res.Add(Cap(p));
        }
        if (res.Count == 0 && text.Trim().Length > 0) res.Add(Cap(text.Trim().TrimEnd('.')));
        return res;
    }

    static string Cap(string s) { return s.Length == 0 ? s : char.ToUpper(s[0], Ru) + s.Substring(1); }

    // ---------------- drawing ----------------
    UIElement DrawToolbar()
    {
        var bar = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        string cur = drawColor ?? cfg.Ink;
        var colors = new List<string> { cfg.Ink };
        colors.AddRange(cfg.TagColors);
        foreach (var c in colors)
        {
            string v = c;
            bool on = !erasing && cur.Equals(v, StringComparison.OrdinalIgnoreCase);
            var e = new Ellipse { Width = 15, Height = 15, Fill = B(v), Margin = new Thickness(0, 2, 7, 2), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                                  Stroke = on ? ink : InkA(60), StrokeThickness = on ? 2.2 : 1 };
            e.MouseLeftButtonDown += (s, ev) => { ev.Handled = true; drawColor = v; erasing = false; ApplyPen(); RenderAdd(); };
            bar.Children.Add(e);
        }
        bar.Children.Add(new Border { Width = 6 });
        foreach (var sz in new double[] { 2.5, 5, 10 })
        {
            double v = sz;
            var holder = new Border { Width = 18, Height = 19, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 3, 0), ToolTip = "Толщина" };
            holder.Child = new Ellipse { Width = v + 2, Height = v + 2, Fill = ink, Opacity = drawSize == v ? 1 : .35, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            holder.MouseLeftButtonDown += (s, ev) => { ev.Handled = true; drawSize = v; erasing = false; ApplyPen(); RenderAdd(); };
            bar.Children.Add(holder);
        }
        bar.Children.Add(new Border { Width = 8 });
        bar.Children.Add(ToolWord("ластик", erasing, () => { erasing = !erasing; ApplyPen(); RenderAdd(); }));
        bar.Children.Add(ToolWord("↶", false, UndoStroke, "Отменить линию (Ctrl+Z)"));
        TextBlock clear = null;
        clear = ToolWord("очистить", false, () =>
        {
            if (!"armed".Equals(clear.Tag)) { clear.Tag = "armed"; clear.Text = "точно?"; clear.Opacity = 1; return; }
            if (canvas != null) canvas.Strokes.Clear();
        });
        bar.Children.Add(clear);
        return bar;
    }

    TextBlock ToolWord(string text, bool on, Action a, string tip = null)
    {
        var t = new TextBlock { Text = text, FontSize = fs * .78, Opacity = on ? 1 : .5, Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        if (on) t.TextDecorations = TextDecorations.Underline;
        t.MouseLeftButtonDown += (s, e) => { e.Handled = true; a(); };
        return t;
    }

    void ApplyPen()
    {
        if (canvas == null) return;
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(drawColor ?? cfg.Ink); } catch { c = Colors.White; }
        canvas.DefaultDrawingAttributes = new System.Windows.Ink.DrawingAttributes
        {
            Color = c, Width = drawSize, Height = drawSize, FitToCurve = cfg.Smooth > 0, IgnorePressure = false,
            StylusTip = System.Windows.Ink.StylusTip.Ellipse
        };
        canvas.EditingMode = erasing ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.Ink;
    }

    UIElement DrawCanvas(Tab tab)
    {
        canvas = new InkCanvas { Background = Brushes.Transparent, ClipToBounds = true };
        if (!string.IsNullOrEmpty(tab.InkData))
        {
            try { using (var ms = new System.IO.MemoryStream(Convert.FromBase64String(tab.InkData))) canvas.Strokes = new System.Windows.Ink.StrokeCollection(ms); }
            catch { }
        }
        ApplyPen();
        var ic = canvas;
        ic.StrokeCollected += (s, e) => { for (int i = 0; i < cfg.Smooth; i++) Smooth(e.Stroke); SaveInk(tab, ic); };
        ic.Strokes.StrokesChanged += (s, e) => SaveInk(tab, ic);
        ic.StrokeErased += (s, e) => SaveInk(tab, ic);
        var g = new Grid();
        var hint = new TextBlock { Text = "рисуй здесь мышкой или пером", Opacity = .3, FontSize = fs * .85, IsHitTestVisible = false,
                                   HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                   Visibility = ic.Strokes.Count == 0 ? Visibility.Visible : Visibility.Collapsed };
        // the hint disappears the moment the pen touches the paper, and comes back only if the drawing is cleared
        ic.PreviewMouseDown += delegate { hint.Visibility = Visibility.Collapsed; };
        ic.PreviewStylusDown += delegate { hint.Visibility = Visibility.Collapsed; };
        ic.Strokes.StrokesChanged += delegate { if (ic.Strokes.Count == 0) hint.Visibility = Visibility.Visible; };
        g.Children.Add(hint);
        g.Children.Add(ic);
        return g;
    }

    // light smoothing: weighted average of neighbours, ends stay where they were drawn
    static void Smooth(System.Windows.Ink.Stroke s)
    {
        var pts = s.StylusPoints;
        if (pts.Count < 5) return;
        var np = new StylusPointCollection(pts.Description);
        np.Add(pts[0]);
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var p = pts[i];
            p.X = (pts[i - 1].X + 2 * p.X + pts[i + 1].X) / 4;
            p.Y = (pts[i - 1].Y + 2 * p.Y + pts[i + 1].Y) / 4;
            np.Add(p);
        }
        np.Add(pts[pts.Count - 1]);
        s.StylusPoints = np;
    }

    void SaveInk(Tab tab, InkCanvas ic)
    {
        try
        {
            if (ic.Strokes.Count == 0) tab.InkData = null;
            else using (var ms = new System.IO.MemoryStream()) { ic.Strokes.Save(ms, true); tab.InkData = Convert.ToBase64String(ms.ToArray()); }
            MarkDirty();
        }
        catch { }
    }

    void UndoStroke()
    {
        if (canvas != null && canvas.Strokes.Count > 0) canvas.Strokes.RemoveAt(canvas.Strokes.Count - 1);
    }

    ContextMenu NewTabMenu()
    {
        var m = new ContextMenu();
        m.Items.Add(MI("Список дел", () => AddTab("list")));
        m.Items.Add(MI("Заметки", () => AddTab("notes")));
        m.Items.Add(MI("Рисунок", () => AddTab("draw")));
        return m;
    }

    // ---------------- main menu ----------------
    static MenuItem MI(string header, Action a, bool check = false)
    {
        var m = new MenuItem { Header = header, IsChecked = check };
        if (a != null) m.Click += delegate { a(); };
        return m;
    }

    ContextMenu MainMenu()
    {
        var m = new ContextMenu();
        var look = new MenuItem { Header = "Внешний вид" };

        var paperM = new MenuItem { Header = "Бумага" };
        foreach (var p in new[] { new[] { "Графитовая доска", "#3A3B3F" }, new[] { "Школьная доска", "#2F4036" }, new[] { "Ночная", "#22262E" }, new[] { "Жёлтый стикер", "#FFE27A" },
                                   new[] { "Розовый", "#FFC4D3" }, new[] { "Голубой", "#BFE4F6" }, new[] { "Мятный", "#C9EBC1" }, new[] { "Белый лист", "#F6F4EF" }, new[] { "Крафт", "#D2BC96" } })
        {
            string v = p[1];
            var mi = MI(p[0], () => { cfg.Paper = v; Changed(); }, cfg.Paper.Equals(v, StringComparison.OrdinalIgnoreCase));
            mi.Icon = new RectShape { Width = 14, Height = 14, Fill = B(v), Stroke = Brushes.Gray, StrokeThickness = .5 };
            paperM.Items.Add(mi);
        }
        paperM.Items.Add(new Separator());
        paperM.Items.Add(MI("Свой цвет…", () => { var c = PickColor(cfg.Paper); if (c != null) { cfg.Paper = c; Changed(); } }));
        look.Items.Add(paperM);

        var inkM = new MenuItem { Header = "Чернила" };
        foreach (var p in new[] { new[] { "Мел", "#ECEAE4" }, new[] { "Графит", "#2A2C3A" }, new[] { "Синяя ручка", "#1E3F99" }, new[] { "Коричневые", "#5B3A24" }, new[] { "Бордо", "#8A1F2C" } })
        {
            string v = p[1];
            var mi = MI(p[0], () => { cfg.Ink = v; Changed(); }, cfg.Ink.Equals(v, StringComparison.OrdinalIgnoreCase));
            mi.Icon = new Ellipse { Width = 12, Height = 12, Fill = B(v), Stroke = Brushes.Gray, StrokeThickness = .5 };
            inkM.Items.Add(mi);
        }
        inkM.Items.Add(new Separator());
        inkM.Items.Add(MI("Свой цвет…", () => { var c = PickColor(cfg.Ink); if (c != null) { cfg.Ink = c; Changed(); } }));
        look.Items.Add(inkM);

        var fontM = new MenuItem { Header = "Почерк" };
        foreach (var f in AvailableFonts())
        {
            string v = f;
            var mi = MI(f, () => { cfg.Font = v; Changed(); }, cfg.Font == f);
            mi.FontFamily = ResolveFont(f); mi.FontSize = 15;
            fontM.Items.Add(mi);
        }
        fontM.Items.Add(new Separator());
        fontM.Items.Add(MI("Свой шрифт: положи .ttf в папку fonts", () => { System.Diagnostics.Process.Start("explorer.exe", "\"" + F("fonts") + "\""); customFonts = null; }));
        look.Items.Add(fontM);

        var sizeM = new MenuItem { Header = "Размер текста" };
        foreach (var v in new double[] { 16, 18, 20, 22, 24, 26, 28, 32 }) { double k = v; sizeM.Items.Add(MI(v.ToString(), () => { cfg.FontSize = k; Changed(); }, cfg.FontSize == v)); }
        look.Items.Add(sizeM);

        var rotM = new MenuItem { Header = "Наклон" };
        foreach (var v in new double[] { -3, -2, -1.5, -1, 0, 1, 1.5, 2, 3 }) { double k = v; rotM.Items.Add(MI((v > 0 ? "+" : "") + v.ToString(Ru) + "°", () => { cfg.Rotation = k; Changed(); }, cfg.Rotation == v)); }
        look.Items.Add(rotM);

        var shM = new MenuItem { Header = "Тень" };
        foreach (var p in new[] { Tuple.Create("Нет", 0.0), Tuple.Create("Лёгкая", 5.0), Tuple.Create("Средняя", 10.0), Tuple.Create("Сильная", 16.0) })
        { var k = p.Item2; shM.Items.Add(MI(p.Item1, () => { cfg.Shadow = k; Changed(); }, cfg.Shadow == k)); }
        look.Items.Add(shM);

        var pinM = new MenuItem { Header = "Крепление" };
        foreach (var p in new[] { new[] { "Кнопка", "push" }, new[] { "Скотч", "tape" }, new[] { "Скрепка", "clip" }, new[] { "Без крепления", "none" } })
        { string v = p[1]; pinM.Items.Add(MI(p[0], () => { cfg.Pin = v; Changed(); }, cfg.Pin == v)); }
        look.Items.Add(pinM);

        var tagM = new MenuItem { Header = "Цветные метки" };
        foreach (var p in new[] { new[] { "Цветной текст", "ink" }, new[] { "Маркер", "marker" }, new[] { "Точка", "dot" } })
        { string v = p[1]; tagM.Items.Add(MI(p[0], () => { cfg.TagStyle = v; Changed(); }, cfg.TagStyle == v)); }
        tagM.Items.Add(new Separator());
        for (int i = 0; i < cfg.TagColors.Count; i++)
        {
            int k = i;
            var mi = MI("Цвет метки " + (i + 1) + "…", () => { var c = PickColor(cfg.TagColors[k]); if (c != null) { cfg.TagColors[k] = c; Changed(); } });
            mi.Icon = new Ellipse { Width = 12, Height = 12, Fill = B(cfg.TagColors[i]) };
            tagM.Items.Add(mi);
        }
        look.Items.Add(tagM);
        m.Items.Add(look);

        var nt = new MenuItem { Header = "Новая вкладка" };
        nt.Items.Add(MI("Список дел", () => AddTab("list")));
        nt.Items.Add(MI("Заметки", () => AddTab("notes")));
        nt.Items.Add(MI("Рисунок", () => AddTab("draw")));
        m.Items.Add(nt);
        var smM = new MenuItem { Header = "Сглаживание рисунков" };
        foreach (var p in new[] { Tuple.Create("Без сглаживания", 0), Tuple.Create("Чуть-чуть", 1), Tuple.Create("Средне", 2), Tuple.Create("Сильно", 4) })
        { int k = p.Item2; smM.Items.Add(MI(p.Item1, () => { cfg.Smooth = k; MarkDirty(); ApplyPen(); }, cfg.Smooth == k)); }
        m.Items.Add(smM);
        m.Items.Add(MI("Удалить выполненные в «" + Cur.Name + "»", () => { Cur.Items.RemoveAll(x => x.Done); expanded = null; MarkDirty(); RenderItems(); }));
        m.Items.Add(new Separator());
        m.Items.Add(MI("Скрывать от записи экрана и стримов", () => { cfg.HideFromCapture = !cfg.HideFromCapture; ApplyCaptureHide(); MarkDirty(); }, cfg.HideFromCapture));
        m.Items.Add(MI("Запускать вместе с Windows", () => SetAutostart(!IsAutostart()), IsAutostart()));
        m.Items.Add(MI("Открыть папку с данными", () => System.Diagnostics.Process.Start("explorer.exe", "\"" + Dir + "\"")));
        m.Items.Add(new Separator());
        m.Items.Add(new MenuItem { Header = cfg.Hotkey + " — показать, Esc — убрать", IsEnabled = false });
        m.Items.Add(new MenuItem { Header = "Ctrl+Tab — вкладки, Ctrl+Z — вернуть удалённое", IsEnabled = false });
        m.Items.Add(MI("Выход", Close));
        return m;
    }

    void Changed() { MarkDirty(); ApplyLook(); }

    static string PickColor(string cur)
    {
        var d = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        try { var c = (Color)ColorConverter.ConvertFromString(cur); d.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B); } catch { }
        if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) return string.Format("#{0:X2}{1:X2}{2:X2}", d.Color.R, d.Color.G, d.Color.B);
        return null;
    }

    // ---------------- storage ----------------
    bool dirty;
    void MarkDirty() { dirty = true; saveTimer.Stop(); saveTimer.Start(); }

    void Flush()
    {
        saveTimer.Stop();
        if (!dirty) return; // nothing changed: no disk write every time the sticker loses focus
        dirty = false;
        WriteJson(F("data.json"), store);
        WriteJson(F("settings.json"), cfg);
    }

    static void WriteJson<T>(string path, T obj)
    {
        try
        {
            string tmp = path + ".tmp";
            using (var fsm = IOFile.Create(tmp))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fsm, new UTF8Encoding(false), false, true, "  "))
                new DataContractJsonSerializer(typeof(T)).WriteObject(w, obj);
            if (IOFile.Exists(path)) IOFile.Replace(tmp, path, null); else IOFile.Move(tmp, path);
        }
        catch { }
    }

    static T ReadJson<T>(string path) where T : class
    {
        try
        {
            if (!IOFile.Exists(path)) return null;
            using (var fsm = IOFile.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(fsm);
        }
        catch
        {
            try { IOFile.Copy(path, path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch { }
            return null;
        }
    }

    void LoadAll()
    {
        cfg = ReadJson<Cfg>(F("settings.json")) ?? new Cfg();
        if (cfg.TagColors == null || cfg.TagColors.Count == 0) cfg.TagColors = new Cfg().TagColors;
        store = ReadJson<Store>(F("data.json"));
        if (store == null || store.Tabs.Count == 0)
        {
            store = new Store();
            var today = new Tab { Name = "Дела", Checklist = true };
            var notes = new Tab { Name = "Заметки", Checklist = false };
            // migrate from DeskNotes 1
            try
            {
                if (IOFile.Exists(F("tasks.txt")))
                    foreach (var raw in IOFile.ReadAllLines(F("tasks.txt"), Encoding.UTF8))
                    {
                        string l = raw.TrimEnd();
                        if (l.Length == 0) continue;
                        bool done = l.StartsWith("[x] ") || l.StartsWith("[X] ");
                        if (l.StartsWith("[x] ") || l.StartsWith("[X] ") || l.StartsWith("[ ] ")) l = l.Substring(4);
                        today.Items.Add(new Item { Title = l, Done = done });
                    }
                if (IOFile.Exists(F("notes.txt")))
                {
                    string n = IOFile.ReadAllText(F("notes.txt"), Encoding.UTF8).Trim();
                    if (n.Length > 0) notes.Items.Add(new Item { Title = "Старые заметки", Details = n });
                }
            }
            catch { }
            notes.Items.Add(new Item
            {
                Title = "Как пользоваться",
                Details = "Ctrl+Alt+N — достать стикер, Esc — убрать обратно.\nКлик по записи — подробности, правый клик — метки и перенос.\nВкладки сверху: двойной клик — переименовать, правый клик — тип и удаление.\n⋯ справа сверху — внешний вид и настройки.",
                Tag = 3
            });
            store.Tabs.Add(today);
            store.Tabs.Add(notes);
        }
        if (!store.DrawTabAdded)
        {
            store.Tabs.Add(new Tab { Name = "Рисунки", Checklist = false, Drawing = true });
            store.DrawTabAdded = true;
        }
    }

    static bool ParseHotkey(string s, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var part in (s ?? "").Split('+'))
        {
            string p = part.Trim();
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= 0x2; break;
                case "alt": mods |= 0x1; break;
                case "shift": mods |= 0x4; break;
                case "win": mods |= 0x8; break;
                default:
                    Key k;
                    if (p.Length == 1 && char.IsDigit(p[0])) p = "D" + p;
                    if (!Enum.TryParse(p, true, out k)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(k);
                    break;
            }
        }
        return vk != 0 && mods != 0;
    }

    static bool IsAutostart()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("DeskNotes") != null;
    }

    static void SetAutostart(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue("DeskNotes", "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
            else k.DeleteValue("DeskNotes", false);
        }
    }
}

