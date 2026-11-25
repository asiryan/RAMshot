using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace GalleryProbe;

// A small target process that keeps generated images in memory so the gallery can be checked manually.
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
            // A two-frame 300x300 GIF checks the animated preview and the byte-exact GIF save.
            byte[] animatedGif = Convert.FromBase64String(AnimatedGifBase64);
            if (FrameCount(animatedGif) < 2) throw new InvalidOperationException("The GIF fixture is not animated");
            Images.Add(animatedGif);
            form.Text = $"RAMshot GalleryProbe ({Images.Count})";
            form.Activate();
            form.TopMost = true;
            form.TopMost = false;
        };
        Application.Run(form);
    }

    private static int FrameCount(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream);
        return image.GetFrameCount(FrameDimension.Time);
    }

    // Two 300x300 frames: pink (200 ms) then blue (300 ms), looping forever.
    private const string AnimatedGifBase64 = """
        R0lGODlhLAEsAYEAAOtaggAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQAFAAAACwAAAAALAEsAQAI/wABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLF
        ixgzatzIsaPHjyBDihxJsqTJkyhTqlzJsqXLlzBjypxJs6bNmzhz6tzJs6fPn0CDCh1KtKjRo0iTKl3KtKnTp1CjSp1KtarVq1izat3KtavXr2DDih1LtqzZ
        s2jTql3Ltq3bt3Djyp1Lt67du3jz6t3Lt6/fv4ADCx5MuLDhw4gTK17MuLHjx5AjS55MubLly5gza97MubPnz6BDix5NurTp06hTq17NurXr17Bjy55Nu7bt
        27hz697Nu7fv38CDCx9OvLjx48iTK1/OvLnz59CjS59Ovbr169iza9/Ovbv37+DDi/YfT768+fPo06tfz769+/fw48ufT7++/fv48+vfz7+///8ABijggAQW
        aOCBCCao4IIMNujggxBGKOGEFFZo4YUYZqjhhhx26OGHIIYo4ogklmjiiSimqOKKLLbo4oswxijjjDTWaOONOOao44489ujjj0AGKeSQRBZp5JFIJqnkkkw2
        6eSTUEYp5ZRUVmnllVhmqeWWXHbp5ZdghinmmGSWaeaZaKap5ppstunmm3DGKeecdNZp55145qnnnnz26eefgAYq6KCEFmrooYgmquiijDbq6KOQRirppJRW
        aumlmGaq6aacdurpp6CGKuqopJZq6qlEBgQAIfkEAR4AAQAsAAAAACwBLAGBPILcAAAAAAAAAAAACP8AAQgcSLCgwYMIEypcyLChw4cQI0qcSLGixYsYM2rc
        yLGjx48gQ4ocSbKkyZMoU6pcybKly5cwY8qcSbOmzZs4c+rcybOnz59AgwodSrSo0aNIkypdyrSp06dQo0qdSrWq1atYs2rdyrWr169gw4odS7as2bNo06pd
        y7at27dw48qdS7eu3bt48+rdy7ev37+AAwseTLiw4cOIEytezLix48eQI0ueTLmy5cuYM2vezLmz58+gQ4seTbq06dOoU6tezbq169ewY8ueTbu27du4c+ve
        zbu379/AgwsfTry48ePIkytfzry58+fQo0ufTr269evYs2vfzr279+/gw4v2H0++vPnz6NOrX8++vfv38OPLn0+/vv37+PPr38+/v///AAYo4IAEFmjggQgm
        qOCCDDbo4IMQRijhhBRWaOGFGGao4YYcdujhhyCGKOKIJJZo4okopqjiiiy26OKLMMYo44w01mjjjTjmqOOOPPbo449ABinkkEQWaeSRSCap5JJMNunkk1BG
        KeWUVFZp5ZVYZqnlllx26eWXYIYp5phklmnmmWimqeaabLbp5ptwxinnnHTWaeedeOap55589unnn4AGKuighBZq6KGIJqrooow26uijkEYq6aSUVmrppZhm
        qummnHbq6aeghirqqKSWauqpRAYEADs=
        """;
}
