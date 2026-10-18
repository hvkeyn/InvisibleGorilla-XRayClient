using System;
using System.Windows;

namespace InvisibleGorillaXRay
{
    internal static class DesktopWindowFit
    {
        public static void ClampToWorkArea(Window window)
        {
            Rect area = SystemParameters.WorkArea;
            double maxWidth = Math.Max(280, area.Width - 8);
            double maxHeight = Math.Max(360, area.Height - 8);
            window.MaxWidth = maxWidth;
            window.MaxHeight = maxHeight;
            if (window.Width > maxWidth)
                window.Width = maxWidth;
            if (window.Height > maxHeight)
                window.Height = maxHeight;
            if (window.MinHeight > window.Height)
                window.MinHeight = window.Height;
            if (window.MinWidth > window.Width)
                window.MinWidth = window.Width;
        }
    }
}
