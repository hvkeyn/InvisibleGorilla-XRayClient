using System.Windows.Controls;
using System.Windows.Media;

namespace InvisibleGorillaXRay.Components
{
    public partial class WifiSignal : UserControl
    {
        private static readonly Brush InactiveBrush =
            new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));

        public WifiSignal()
        {
            InitializeComponent();
        }

        /// <summary>Applies signal level 0..4 with the given active color. The dot always shows the status color.</summary>
        public void SetSignal(int level, Brush activeBrush)
        {
            dot.Fill = activeBrush;
            arcInner.Stroke = level >= 2 ? activeBrush : InactiveBrush;
            arcMiddle.Stroke = level >= 3 ? activeBrush : InactiveBrush;
            arcOuter.Stroke = level >= 4 ? activeBrush : InactiveBrush;
        }
    }
}
