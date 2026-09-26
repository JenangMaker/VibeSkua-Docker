// The part of WinForms that auqw/Scripts' CoreBots.cs uses (a progress
// dialog in one of its joke branches), so that scripts compile on Linux.
// Showing a form is not possible here: Application.Run throws, and the
// caller's own try/catch logs it. Add members only as scripts need them.

using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms;

public delegate void MethodInvoker();

public delegate void PaintEventHandler(object? sender, PaintEventArgs e);

public class PaintEventArgs : EventArgs { }

public enum DockStyle { None, Top, Bottom, Left, Right, Fill }

public enum FormStartPosition { Manual, CenterScreen, WindowsDefaultLocation, WindowsDefaultBounds, CenterParent }

public enum FormBorderStyle { None, FixedSingle, Fixed3D, FixedDialog, Sizable, FixedToolWindow, SizableToolWindow }

public enum ProgressBarStyle { Blocks, Continuous, Marquee }

public class Control : Component
{
    public string Text { get; set; } = "";
    public Size Size { get; set; }
    public int Width { get => Size.Width; set => Size = new Size(value, Size.Height); }
    public int Height { get => Size.Height; set => Size = new Size(Size.Width, value); }
    public DockStyle Dock { get; set; }
    public Color ForeColor { get; set; }
    public Color BackColor { get; set; }
    public ControlCollection Controls { get; } = new();

#pragma warning disable CS0067 // never raised: nothing is ever shown
    public event PaintEventHandler? Paint;
#pragma warning restore CS0067

    public object? Invoke(Delegate method) => method.DynamicInvoke();

    public class ControlCollection : List<Control> { }
}

public class Form : Control
{
    public FormStartPosition StartPosition { get; set; }
    public FormBorderStyle FormBorderStyle { get; set; }
    public bool MaximizeBox { get; set; }
    public bool MinimizeBox { get; set; }

#pragma warning disable CS0067
    public event EventHandler? Shown;
#pragma warning restore CS0067

    public void Close() { }
}

public class ProgressBar : Control
{
    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100;
    public int Value { get; set; }
    public ProgressBarStyle Style { get; set; }
}

public static class Application
{
    public static void Run(Form form) =>
        throw new PlatformNotSupportedException("WinForms is not available in Skua.Host");
}
