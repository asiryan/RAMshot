using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace GalleryProbe;

// A small target process that keeps generated PNGs in memory so the gallery can be checked manually.
internal static class Program
{
    private static readonly List<byte[]> Images = new();

    [STAThread]
    private static void Main()
    {
        var form = new Form { Text = "RAMshot GalleryProbe", Width = 460, Height = 340, StartPosition = FormStartPosition.CenterScreen };
        form.Shown += (_, _) =>
        {
            var colors = new[]
            {
                Color.FromArgb(230, 70, 70), Color.FromArgb(240, 150, 40), Color.FromArgb(235, 215, 55),
                Color.FromArgb(80, 190, 90), Color.FromArgb(60, 190, 180), Color.FromArgb(70, 140, 230),
                Color.FromArgb(110, 90, 220), Color.FromArgb(190, 90, 200), Color.FromArgb(230, 90, 160),
                Color.FromArgb(120, 120, 120), Color.FromArgb(90, 60, 30), Color.FromArgb(20, 40, 80)
            };
            foreach (Color color in colors)
            {
                using var bitmap = new Bitmap(300, 300);
                using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                Images.Add(stream.ToArray());
            }
            form.Text = $"RAMshot GalleryProbe ({Images.Count})";
            form.Activate();
            form.TopMost = true;
            form.TopMost = false;
        };
        Application.Run(form);
    }
}
