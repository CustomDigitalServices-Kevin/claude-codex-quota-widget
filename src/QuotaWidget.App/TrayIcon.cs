using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace QuotaWidget.App;

internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon notify = new();
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Func<WidgetMenuItem[]> menuItems;
    private Icon? icon;
    private IconPresentation? current;

    public TrayIcon(Action toggle, Func<WidgetMenuItem[]> menuItems)
    {
        this.menuItems = menuItems;
        RefreshMenu();
        menu.Opening += (_, _) => RefreshMenu();
        notify.ContextMenuStrip = menu;
        notify.DoubleClick += (_, _) => toggle();
    }

    private void RefreshMenu()
    {
        while (menu.Items.Count > 0)
        {
            var old = menu.Items[0];
            menu.Items.RemoveAt(0);
            old.Dispose();
        }
        foreach (var entry in menuItems()) { menu.Items.Add(CreateItem(entry)); }
    }

    private static Forms.ToolStripMenuItem CreateItem(WidgetMenuItem entry)
    {
        var item = new Forms.ToolStripMenuItem(entry.Label) { Checked = entry.Checked == true };
        if (entry.Execute is { } action) { item.Click += (_, _) => action(); }
        foreach (var child in entry.Children ?? []) { item.DropDownItems.Add(CreateItem(child)); }
        return item;
    }

    public void ShowBalloon(string text) => notify.ShowBalloonTip(10000, "Quotas IA", text, Forms.ToolTipIcon.Info);

    public void Update(CardPresentation presentation)
    {
        notify.Text = presentation.ToolTip;
        if (current != presentation.Icon)
        {
            var replacement = Draw(presentation.Icon);
            var previous = icon;
            notify.Icon = replacement;
            icon = replacement;
            previous?.Dispose();
            current = presentation.Icon;
        }
        notify.Visible = true;
    }

    private static Icon Draw(IconPresentation presentation)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(presentation.Color switch
        {
            "vert" => Color.FromArgb(32, 120, 65), "orange" => Color.FromArgb(165, 90, 0),
            "rouge" => Color.FromArgb(185, 40, 45), _ => Color.FromArgb(80, 85, 95),
        });
        using var font = new Font("Segoe UI", presentation.Text.Length > 2 ? 13 : 18, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(presentation.Text, font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
        var handle = bitmap.GetHicon();
        try
        {
            // FromHandle does not own HICON. Clone first, then release the original explicitly.
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    public void Dispose()
    {
        notify.Visible = false;
        notify.Dispose();
        icon?.Dispose();
        menu.Dispose();
    }
}
