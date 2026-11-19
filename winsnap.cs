// winsnap: one click grabs the whole desktop (all monitors), drag to crop, then save or copy
// and exit. See .claude/architecture.md. Built by build.cmd with the csc.exe that ships with
// Windows, which compiles C# 5 only: no $"..." interpolation, ?., => members, nameof or out var.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]   // the clipboard needs it
    static void Main()
    {
        // First, before anything reads a screen size: otherwise at 150% scaling Windows reports
        // scaled sizes and the shot covers only the top-left two thirds of the desktop.
        SetProcessDPIAware();

        // Taken before any winsnap window exists, so winsnap is never in the shot. The virtual
        // screen can start at negative coordinates (a monitor left of or above the main one).
        Rectangle vs = SystemInformation.VirtualScreen;
        Bitmap shot = new Bitmap(vs.Width, vs.Height, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(shot))
            g.CopyFromScreen(vs.Location, Point.Empty, vs.Size);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SnapForm(shot));
    }
}

class SnapForm : Form
{
    const int DragStart = 4;   // screen px the pointer must go, on either axis, before a press is a drag
    const int MinCrop = 2;     // image px; a crop narrower or shorter than this is ignored as accidental

    readonly List<Bitmap> stack = new List<Bitmap>();   // [0] the original, the last one is shown
    readonly PictureBox box = new PictureBox();
    readonly Pen selDark = new Pen(Color.Black, 2);
    readonly Pen selLight = new Pen(Color.White, 2);

    bool pressed, dragging;
    Point start;              // where the left button went down, box coordinates
    Rectangle sel;            // selection outline, box coordinates; Empty when there is none

    public SnapForm(Bitmap shot)
    {
        stack.Add(shot);

        Font = SystemFonts.MessageBoxFont;
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(wa.Width * 3 / 4, wa.Height * 3 / 4);   // for when it is un-maximized
        WindowState = FormWindowState.Maximized;

        selLight.DashPattern = new float[] { 3, 3 };   // even black and white dashes

        box.Dock = DockStyle.Fill;
        box.SizeMode = PictureBoxSizeMode.Zoom;
        box.BackColor = Color.FromArgb(48, 48, 48);
        box.Cursor = Cursors.Cross;
        box.Image = shot;
        box.MouseDown += BoxMouseDown;
        box.MouseMove += BoxMouseMove;
        box.MouseUp += BoxMouseUp;
        box.MouseCaptureChanged += BoxCaptureChanged;
        box.Paint += BoxPaint;

        FlowLayoutPanel bar = new FlowLayoutPanel();
        bar.Dock = DockStyle.Top;
        bar.AutoSize = true;
        bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        bar.Padding = new Padding(4);
        // Buttons sit at the right end. RightToLeft lays the first one added rightmost, so Copy
        // goes in first to read "Save  Copy" left to right; TabIndex keeps Save first for focus.
        bar.FlowDirection = FlowDirection.RightToLeft;
        Button save = MakeButton("Save (Ctrl+S)", Save);
        Button copy = MakeButton("Copy (Ctrl+C)", Copy);
        save.TabIndex = 0;
        copy.TabIndex = 1;
        bar.Controls.Add(copy);
        bar.Controls.Add(save);

        // Docking goes last-added first: the bar takes the top, the picture the rest.
        Controls.Add(box);
        Controls.Add(bar);
        UpdateTitle();
    }

    static Button MakeButton(string text, Action onClick)
    {
        Button b = new Button();
        b.Text = text;
        b.AutoSize = true;
        b.Click += delegate { onClick(); };
        return b;
    }

    Bitmap Current { get { return stack[stack.Count - 1]; } }

    void UpdateTitle()
    {
        Text = "winsnap - " + (stack.Count > 1 ? "drag to crop, click to undo a crop" : "drag to crop") +
               " - " + Current.Width + " x " + Current.Height;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.S: Save(); return true;
            case Keys.Control | Keys.C: Copy(); return true;
            case Keys.Escape: Close(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- crop (claude3's cropDown / cropMove / cropUp) ----

    void BoxMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        // WinForms captures the mouse to the box on button down, so a drag can end outside the window.
        pressed = true;
        dragging = false;
        start = e.Location;
    }

    void BoxMouseMove(object sender, MouseEventArgs e)
    {
        // Track the press ourselves: WinForms fills e.Button on a move from the live key state.
        if (!pressed) return;
        if (!dragging && Math.Abs(e.X - start.X) < DragStart && Math.Abs(e.Y - start.Y) < DragStart) return;
        dragging = true;
        ShowSelection(ToBox(CropRect(start, e.Location)));
    }

    void BoxMouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !pressed) return;
        pressed = false;
        if (!dragging) { Undo(); return; }
        dragging = false;
        ShowSelection(Rectangle.Empty);
        Rectangle r = CropRect(start, e.Location);
        if (r.Width >= MinCrop && r.Height >= MinCrop) Push(Current.Clone(r, Current.PixelFormat));
    }

    // Capture taken away mid-drag (Alt+Tab, say): drop the drag and crop nothing.
    void BoxCaptureChanged(object sender, EventArgs e)
    {
        if (!pressed || box.Capture) return;
        pressed = dragging = false;
        ShowSelection(Rectangle.Empty);
    }

    void Push(Bitmap b)
    {
        stack.Add(b);
        box.Image = b;
        UpdateTitle();
    }

    void Undo()
    {
        if (stack.Count < 2) return;
        Bitmap popped = Current;
        stack.RemoveAt(stack.Count - 1);
        box.Image = Current;      // off the box before it is disposed
        popped.Dispose();
        UpdateTitle();
    }

    // ---- mouse to image pixels (claude3's cropRect) ----

    // Zoom letterboxes the picture inside the box, so the box alone doesn't say where it is.
    void Fit(out float scale, out float left, out float top)
    {
        Bitmap img = Current;
        Size b = box.ClientSize;
        scale = Math.Min((float)b.Width / img.Width, (float)b.Height / img.Height);
        left = (b.Width - img.Width * scale) / 2;
        top = (b.Height - img.Height * scale) / 2;
    }

    // The drag from a to b (box coordinates) in image pixels. Both ends are clamped onto the
    // picture, so a drag that starts or ends off it crops to its edge.
    Rectangle CropRect(Point a, Point b)
    {
        float scale, left, top;
        Fit(out scale, out left, out top);
        Bitmap img = Current;
        float ax = ToImage(a.X, left, img.Width, scale), bx = ToImage(b.X, left, img.Width, scale);
        float ay = ToImage(a.Y, top, img.Height, scale), by = ToImage(b.Y, top, img.Height, scale);
        Rectangle r = new Rectangle(Round(Math.Min(ax, bx)), Round(Math.Min(ay, by)),
                                    Round(Math.Abs(bx - ax)), Round(Math.Abs(by - ay)));
        // Rounding can push it a pixel past the edge, and Clone throws on that.
        return Rectangle.Intersect(r, new Rectangle(Point.Empty, img.Size));
    }

    static float ToImage(int v, float offset, int length, float scale)
    {
        return (Math.Max(offset, Math.Min(v, offset + length * scale)) - offset) / scale;
    }

    // An image-pixel rectangle back on the box, where the selection outline goes.
    Rectangle ToBox(Rectangle r)
    {
        float scale, left, top;
        Fit(out scale, out left, out top);
        return new Rectangle(Round(left + r.X * scale), Round(top + r.Y * scale),
                             Round(r.Width * scale), Round(r.Height * scale));
    }

    static int Round(float v) { return (int)Math.Round(v, MidpointRounding.AwayFromZero); }

    // ---- selection outline ----

    void ShowSelection(Rectangle onBox)
    {
        // Repaint only around the old and new outlines: redrawing the whole zoomed shot on
        // every mouse move would lag.
        if (!sel.IsEmpty) box.Invalidate(Rectangle.Inflate(sel, 3, 3));
        sel = onBox;
        if (!sel.IsEmpty) box.Invalidate(Rectangle.Inflate(sel, 3, 3));
    }

    void BoxPaint(object sender, PaintEventArgs e)
    {
        if (sel.IsEmpty) return;
        e.Graphics.DrawRectangle(selDark, sel);     // black and white dashes show on any picture
        e.Graphics.DrawRectangle(selLight, sel);
    }

    // ---- save, copy ----

    void Save()
    {
        using (SaveFileDialog d = new SaveFileDialog())
        {
            d.Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg";
            d.DefaultExt = "png";
            d.FileName = "winsnap-" + DateTime.Now.ToString("yy-MM-dd-HH-mm-ss") + ".png";
            string dir = ScreenshotsFolder();
            if (dir != null) d.InitialDirectory = dir;
            if (d.ShowDialog(this) != DialogResult.OK) return;   // cancel: back to the window

            // The format follows the chosen filter, unless the name typed says otherwise
            // (foo.jpg with the PNG filter still selected is a JPEG).
            string ext = Path.GetExtension(d.FileName).ToLowerInvariant();
            bool jpeg = ext == ".jpg" || ext == ".jpeg" || (ext != ".png" && d.FilterIndex == 2);
            try
            {
                if (jpeg) SaveJpeg(Current, d.FileName);
                else Current.Save(d.FileName, ImageFormat.Png);
            }
            catch (Exception ex) { Fail("save", ex); return; }
        }
        Close();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id,
        uint flags, IntPtr token, out IntPtr path);

    static readonly Guid FOLDERID_Screenshots = new Guid("b7bede81-df94-4682-a7d8-57a52620b86f");
    const uint KF_FLAG_CREATE = 0x8000;

    // Windows' Screenshots folder, where Win+PrtScn saves (it moves with OneDrive, and can be
    // named "Screenshots 1"), made if missing. Falls back to Pictures\Screenshots, then null
    // (the dialog then opens wherever Windows would have).
    static string ScreenshotsFolder()
    {
        IntPtr p;
        int hr = SHGetKnownFolderPath(FOLDERID_Screenshots, KF_FLAG_CREATE, IntPtr.Zero, out p);
        string known = hr == 0 ? Marshal.PtrToStringUni(p) : null;
        Marshal.FreeCoTaskMem(p);               // the caller frees it even on failure
        if (known != null) return known;
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception) { return null; }
    }

    static void SaveJpeg(Bitmap img, string path)
    {
        ImageCodecInfo codec = null;
        foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
            if (c.FormatID == ImageFormat.Jpeg.Guid) codec = c;
        using (EncoderParameters ps = new EncoderParameters(1))
        {
            ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
            img.Save(path, codec, ps);
        }
    }

    void Copy()
    {
        // SetImage copies the data onto the clipboard, so the picture outlives winsnap.
        try { Clipboard.SetImage(Current); }
        catch (Exception ex) { Fail("copy", ex); return; }
        Close();
    }

    void Fail(string what, Exception ex)
    {
        MessageBox.Show(this, "Could not " + what + " the picture:\n" + ex.Message, "winsnap",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
