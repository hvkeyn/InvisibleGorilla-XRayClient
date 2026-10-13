using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Android.Content;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace InvisibleGorillaXRay.Android.Views
{
    /// <summary>
    /// Touch selection for data fields. The page scroller otherwise takes the drag,
    /// so a bridge line or an OpenFlux link cannot be highlighted.
    /// </summary>
    internal static class TextSelectionAssist
    {
        private static readonly ConditionalWeakTable<TextBox, HoldState> Holds = new();
        private static readonly ConditionalWeakTable<ScrollViewer, ScrollLock> Locks = new();
        private static readonly HashSet<ScrollViewer> HookedScrolls = new();
        private static readonly SolidColorBrush SelectionFill = new(Color.FromArgb(150, 109, 204, 142));

        public static void Attach(Control root)
        {
            foreach (var node in root.GetLogicalDescendants())
            {
                if (node is TextBox box)
                    HookBox(box);
                else if (node is ScrollViewer scroll && Ancestor<TextBox>(scroll) == null)
                    HookScroll(scroll);
            }
        }

        private static void HookBox(TextBox box)
        {
            if (box.Classes.Contains("selection-ready"))
                return;

            box.Classes.Add("selection-ready");
            box.SelectionBrush = SelectionFill;
            box.SelectionForegroundBrush = Brushes.White;
            box.CaretBrush = Brushes.White;
            box.ContextMenu = null;
            box.ContextFlyout = BuildFlyout(box);
            box.ContextRequested += (_, _) =>
            {
                if (box.SelectionStart == box.SelectionEnd && !string.IsNullOrEmpty(box.Text))
                    box.SelectAll();
            };
            box.DoubleTapped += (_, e) =>
            {
                if (box.IsReadOnly || !box.AcceptsReturn)
                {
                    box.Focus();
                    box.SelectAll();
                    e.Handled = true;
                }
            };
            box.AddHandler(InputElement.PointerPressedEvent, (_, e) => BeginHold(box, e), RoutingStrategies.Tunnel, true);
            box.AddHandler(InputElement.PointerMovedEvent, (_, e) => TrackHold(box, e), RoutingStrategies.Tunnel, true);
            box.AddHandler(InputElement.PointerReleasedEvent, (_, _) => EndHold(box, showIfDragSelected: true), RoutingStrategies.Tunnel, true);
            box.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => EndHold(box, showIfDragSelected: false), RoutingStrategies.Tunnel, true);
        }

        private static MenuFlyout BuildFlyout(TextBox box)
        {
            var selectAll = new MenuItem();
            var copy = new MenuItem();
            var cut = new MenuItem();
            var paste = new MenuItem();
            selectAll.Click += (_, _) =>
            {
                box.Focus();
                box.SelectAll();
            };
            copy.Click += (_, _) => Copy(box);
            cut.Click += (_, _) =>
            {
                if (box.IsReadOnly)
                    return;
                Copy(box);
                DeleteSelection(box);
            };
            paste.Click += (_, _) => Paste(box);

            var flyout = new MenuFlyout();
            flyout.Opening += (_, _) =>
            {
                selectAll.Header = Label("Lang.Text.SelectAll", "Select all");
                copy.Header = Label("Lang.Text.Copy", "Copy");
                cut.Header = Label("Lang.Text.Cut", "Cut");
                paste.Header = Label("Lang.Text.Paste", "Paste");
                bool hasText = !string.IsNullOrEmpty(box.Text);
                bool hasSelection = box.SelectionStart != box.SelectionEnd;
                selectAll.IsEnabled = hasText;
                copy.IsEnabled = hasText;
                cut.IsEnabled = hasSelection && !box.IsReadOnly;
                paste.IsEnabled = !box.IsReadOnly;
            };
            flyout.Items.Add(selectAll);
            flyout.Items.Add(copy);
            flyout.Items.Add(cut);
            flyout.Items.Add(paste);
            return flyout;
        }

        private static void BeginHold(TextBox box, PointerPressedEventArgs e)
        {
            var state = Holds.GetOrCreateValue(box);
            state.Timer?.Stop();
            state.Moved = false;
            state.MenuShown = false;
            state.Origin = e.GetPosition(box);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            state.Timer = timer;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (state.Moved || state.MenuShown)
                    return;
                state.MenuShown = true;
                if (box.SelectionStart == box.SelectionEnd && !string.IsNullOrEmpty(box.Text))
                    box.SelectAll();
                try { box.ContextFlyout?.ShowAt(box); } catch { }
            };
            timer.Start();
        }

        private static void TrackHold(TextBox box, PointerEventArgs e)
        {
            if (!Holds.TryGetValue(box, out var state) || state.Timer == null)
                return;
            Point now = e.GetPosition(box);
            if (Math.Abs(now.X - state.Origin.X) > 12 || Math.Abs(now.Y - state.Origin.Y) > 12)
            {
                state.Moved = true;
                state.Timer.Stop();
                state.Timer = null;
            }
        }

        private static void EndHold(TextBox box, bool showIfDragSelected)
        {
            if (!Holds.TryGetValue(box, out var state))
                return;
            state.Timer?.Stop();
            state.Timer = null;
            if (!showIfDragSelected || state.MenuShown || !state.Moved)
                return;
            if (box.SelectionStart == box.SelectionEnd)
                return;
            state.MenuShown = true;
            Dispatcher.UIThread.Post(() =>
            {
                try { box.ContextFlyout?.ShowAt(box); } catch { }
            });
        }

        private static void HookScroll(ScrollViewer scroll)
        {
            if (!HookedScrolls.Add(scroll))
                return;

            scroll.AddHandler(InputElement.PointerPressedEvent, OnScrollPressed, RoutingStrategies.Bubble, true);
            scroll.AddHandler(InputElement.PointerMovedEvent, OnScrollMoved, RoutingStrategies.Bubble, true);
            scroll.AddHandler(InputElement.PointerReleasedEvent, OnScrollReleased, RoutingStrategies.Bubble, true);
        }

        private static void OnScrollPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not ScrollViewer scroll)
                return;
            TextBox? box = BoxFrom(e.Source);
            if (box == null)
                return;

            var state = Locks.GetOrCreateValue(scroll);
            state.Active = true;
            state.Offset = scroll.Offset;
            if (e.Source is IInputElement target)
                e.Pointer.Capture(target);
            else
                e.Pointer.Capture(box);
        }

        private static void OnScrollMoved(object? sender, PointerEventArgs e)
        {
            if (sender is not ScrollViewer scroll)
                return;
            if (!Locks.TryGetValue(scroll, out var state) || !state.Active)
                return;
            scroll.Offset = state.Offset;
        }

        private static void OnScrollReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (sender is not ScrollViewer scroll)
                return;
            if (Locks.TryGetValue(scroll, out var state))
                state.Active = false;
        }

        private static TextBox? BoxFrom(object? source)
        {
            if (source is TextBox box)
                return box;
            var visual = source as Visual;
            while (visual != null)
            {
                if (visual is TextBox found)
                    return found;
                visual = visual.GetVisualParent();
            }

            return null;
        }

        private static T? Ancestor<T>(ILogical? node) where T : class
        {
            while (node != null)
            {
                if (node is T match)
                    return match;
                node = node.LogicalParent;
            }

            return null;
        }

        private static void Copy(TextBox box)
        {
            string text = box.SelectionStart != box.SelectionEnd
                ? box.SelectedText ?? ""
                : box.Text ?? "";
            if (string.IsNullOrEmpty(text))
                return;

            try
            {
                var clipboard = TopLevel.GetTopLevel(box)?.Clipboard;
                if (clipboard != null)
                {
                    _ = clipboard.SetTextAsync(text);
                    return;
                }
            }
            catch
            {
            }

            try
            {
                if (global::Android.App.Application.Context?.GetSystemService(Context.ClipboardService) is ClipboardManager manager)
                    manager.PrimaryClip = ClipData.NewPlainText("text", text);
            }
            catch
            {
            }
        }

        private static async void Paste(TextBox box)
        {
            if (box.IsReadOnly)
                return;

            string? text = null;
            try
            {
                text = await (TopLevel.GetTopLevel(box)?.Clipboard?.GetTextAsync() ?? System.Threading.Tasks.Task.FromResult<string?>(null));
            }
            catch
            {
                text = null;
            }

            if (string.IsNullOrEmpty(text))
            {
                try
                {
                    if (global::Android.App.Application.Context?.GetSystemService(Context.ClipboardService) is ClipboardManager manager
                        && manager.PrimaryClip is { ItemCount: > 0 } clip)
                    {
                        text = clip.GetItemAt(0)?.Text;
                        if (string.IsNullOrWhiteSpace(text))
                            text = clip.GetItemAt(0)?.CoerceToText(global::Android.App.Application.Context)?.ToString();
                    }
                }
                catch
                {
                    text = null;
                }
            }

            if (string.IsNullOrEmpty(text))
                return;

            Insert(box, text);
        }

        private static void DeleteSelection(TextBox box)
        {
            if (box.SelectionStart == box.SelectionEnd)
                return;
            Insert(box, "");
        }

        private static void Insert(TextBox box, string insert)
        {
            string current = box.Text ?? "";
            int start = Math.Clamp(Math.Min(box.SelectionStart, box.SelectionEnd), 0, current.Length);
            int end = Math.Clamp(Math.Max(box.SelectionStart, box.SelectionEnd), 0, current.Length);
            box.Text = current.Remove(start, end - start).Insert(start, insert);
            box.CaretIndex = start + insert.Length;
            box.SelectionStart = box.CaretIndex;
            box.SelectionEnd = box.CaretIndex;
        }

        private static string Label(string key, string fallback)
        {
            if (Application.Current?.TryFindResource(key, out object? value) == true && value is string text && text.Length > 0)
                return text;
            return fallback;
        }

        private sealed class HoldState
        {
            public DispatcherTimer? Timer;
            public Point Origin;
            public bool Moved;
            public bool MenuShown;
        }

        private sealed class ScrollLock
        {
            public bool Active;
            public Vector Offset;
        }
    }
}
