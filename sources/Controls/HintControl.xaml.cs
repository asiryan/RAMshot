using System.Windows.Controls;

namespace RAMshot.Controls;

public partial class HintControl : UserControl
{
    public HintControl() => InitializeComponent();

    public string Text
    {
        get => HintText.Text;
        set => HintText.Text = value;
    }
}
