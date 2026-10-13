using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace InvisibleGorillaXRay
{
    /// <summary>
    /// Keeps bridge lines, OpenFlux fields, and the other data boxes selectable.
    /// A read-only single-line value is selected on the first click so it can be copied.
    /// </summary>
    internal static class TextSelectionAssist
    {
        private static readonly HashSet<TextBox> Ready = new();
        private static readonly SolidColorBrush SelectionFill = new(Color.FromArgb(150, 109, 204, 142));

        public static void Attach(DependencyObject root)
        {
            if (root is TextBox box)
                Prepare(box);

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
                Attach(VisualTreeHelper.GetChild(root, i));
        }

        private static void Prepare(TextBox box)
        {
            if (!Ready.Add(box))
                return;

            box.SelectionBrush = SelectionFill;
            box.SelectionOpacity = 1;
            box.IsInactiveSelectionHighlightEnabled = true;
            box.CaretBrush = Brushes.White;
            if (box.IsReadOnly)
                box.IsReadOnlyCaretVisible = true;

            box.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (!box.IsReadOnly || box.AcceptsReturn || box.IsKeyboardFocusWithin)
                    return;
                box.Focus();
                box.SelectAll();
                e.Handled = true;
            };
        }
    }
}
