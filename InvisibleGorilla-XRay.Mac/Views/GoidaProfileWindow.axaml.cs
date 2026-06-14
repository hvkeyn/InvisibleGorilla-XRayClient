using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using InvisibleGorillaXRay.Handlers;
using InvisibleGorillaXRay.Models;
using InvisibleGorillaXRay.Services;
using InvisibleGorillaXRay.Services.Goida;
using InvisibleGorillaXRay.Values;

namespace InvisibleGorillaXRay.Mac.Views
{
    public partial class GoidaProfileWindow : Window
    {
        private sealed class NodeRow
        {
            public int ListId { get; init; }
            public string Id { get; init; } = string.Empty;
            public string DisplayName { get; init; } = string.Empty;
            public string Endpoint { get; init; } = string.Empty;
            public string LatencyText { get; init; } = string.Empty;
            public string StatusText { get; init; } = string.Empty;
            public string ActiveMark { get; init; } = string.Empty;
        }

        private GoidaProfileHandler? goidaHandler;
        private Func<UserSettings>? getUserSettings;
        private Action<UserSettings>? onUpdateUserSettings;
        private Action<GoidaNode>? onActiveNodeChanged;
        private bool isApplyingSettings;
        private bool initialLoadStarted;

        private LocalizationService LocalizationService => ServiceLocator.Get<LocalizationService>();

        public GoidaProfileWindow()
        {
            InitializeComponent();
        }

        public void Setup(
            GoidaProfileHandler goidaHandler,
            Func<UserSettings> getUserSettings,
            Action<UserSettings> onUpdateUserSettings,
            Action<GoidaNode> onActiveNodeChanged)
        {
            this.goidaHandler = goidaHandler;
            this.getUserSettings = getUserSettings;
            this.onUpdateUserSettings = onUpdateUserSettings;
            this.onActiveNodeChanged = onActiveNodeChanged;

            // Guard init: populating the combo fires SelectionChanged synchronously,
            // which would save default control values over the user's settings.
            isApplyingSettings = true;
            try
            {
                PopulateSelectionModes();
            }
            finally
            {
                isApplyingSettings = false;
            }

            ApplySettingsToControls();
            RefreshList();
            UpdateStatusSummary();

            goidaHandler.Manager.NodesUpdated += OnNodesUpdated;
            goidaHandler.Manager.StatusMessage += OnStatusMessage;
            Opened += OnWindowOpened;
            Closed += (_, __) =>
            {
                goidaHandler.Manager.NodesUpdated -= OnNodesUpdated;
                goidaHandler.Manager.StatusMessage -= OnStatusMessage;
            };
        }

        private async void OnWindowOpened(object? sender, EventArgs e)
        {
            if (initialLoadStarted || goidaHandler == null)
                return;

            initialLoadStarted = true;
            await LoadNodesOnOpenAsync();
        }

        private async Task LoadNodesOnOpenAsync()
        {
            if (goidaHandler == null)
                return;

            if (goidaHandler.Manager.GetNodesSorted().Count > 0)
            {
                UpdateStatusSummary();
                return;
            }

            SetStatusText(Localize("Lang.Goida.Loading"));
            try
            {
                await goidaHandler.Manager.RefreshListsAsync().ConfigureAwait(true);
                RefreshList();
                UpdateStatusSummary();
            }
            catch
            {
                SetStatusText(Localize("Lang.Goida.RefreshFailed"));
            }
        }

        private void OnStatusMessage(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (string.Equals(message, "refresh-failed", StringComparison.OrdinalIgnoreCase))
                    SetStatusText(Localize("Lang.Goida.RefreshFailed"));
                else if (string.Equals(message, "refresh-complete", StringComparison.OrdinalIgnoreCase))
                    UpdateStatusSummary();
            });
        }

        private void SetStatusText(string text)
        {
            textBlockStatus.Text = text ?? string.Empty;
        }

        private int? GetActiveListFilter()
        {
            if (getUserSettings == null)
                return null;

            return GoidaProfileManager.ResolveActiveListFilter(
                textBoxFilterList.Text,
                getUserSettings().GetGoidaSettings());
        }

        private void UpdateStatusSummary()
        {
            if (goidaHandler == null)
                return;

            int count = goidaHandler.Manager.GetVisibleNodes(GetActiveListFilter()).Count;
            SetStatusText(count == 0
                ? Localize("Lang.Goida.EmptyHint")
                : string.Format(Localize("Lang.Goida.NodesLoaded"), count));
        }

        private void PopulateSelectionModes()
        {
            comboBoxSelectionMode.ItemsSource = new[]
            {
                Localize("Lang.Goida.Mode.AutoBest"),
                Localize("Lang.Goida.Mode.ManualFixed"),
                Localize("Lang.Goida.Mode.ManualPool")
            };
            comboBoxSelectionMode.SelectedIndex = 0;
        }

        private void ApplySettingsToControls()
        {
            if (getUserSettings == null)
                return;

            isApplyingSettings = true;
            try
            {
                GoidaProfileSettings settings = getUserSettings().GetGoidaSettings();
                checkBoxEnabled.IsChecked = settings.Enabled;
                checkBoxAutoSwitch.IsChecked = settings.AutoSwitchOnFly;
                comboBoxSelectionMode.SelectedIndex = settings.SelectionMode switch
                {
                    GoidaSelectionMode.ManualFixed => 1,
                    GoidaSelectionMode.ManualPool => 2,
                    _ => 0
                };
            }
            finally
            {
                isApplyingSettings = false;
            }
        }

        private void CaptureSettingsFromControls()
        {
            if (isApplyingSettings || getUserSettings == null || onUpdateUserSettings == null)
                return;

            UserSettings current = getUserSettings();
            GoidaProfileSettings settings = current.GetGoidaSettings().Clone();
            settings.Enabled = checkBoxEnabled.IsChecked == true;
            settings.AutoSwitchOnFly = checkBoxAutoSwitch.IsChecked == true;
            settings.SelectionMode = comboBoxSelectionMode.SelectedIndex switch
            {
                1 => GoidaSelectionMode.ManualFixed,
                2 => GoidaSelectionMode.ManualPool,
                _ => GoidaSelectionMode.AutoBest
            };

            current.Goida = settings;
            onUpdateUserSettings(current);
            goidaHandler?.Manager.UpdateSettings(settings);
        }

        private void RefreshList()
        {
            if (goidaHandler == null || getUserSettings == null)
                return;

            GoidaProfileSettings settings = getUserSettings().GetGoidaSettings();
            int? listFilter = int.TryParse(textBoxFilterList.Text?.Trim(), out int listId) && listId >= 1 && listId <= 26
                ? listId
                : null;

            List<NodeRow> rows = goidaHandler.Manager.GetNodesSorted()
                .Where(node => listFilter == null || node.ListId == listFilter)
                .Select(node => new NodeRow
                {
                    ListId = node.ListId,
                    Id = node.Id,
                    DisplayName = node.DisplayName,
                    Endpoint = node.Endpoint,
                    LatencyText = FormatLatency(node.LatencyMs),
                    StatusText = FormatStatus(node.Status),
                    ActiveMark = string.Equals(node.Id, settings.ActiveNodeId, StringComparison.OrdinalIgnoreCase)
                        ? "*"
                        : string.Empty
                })
                .ToList();

            listNodes.ItemsSource = rows;
        }

        private static string FormatLatency(int latencyMs)
        {
            return latencyMs switch
            {
                Availability.NOT_CHECKED => "-",
                Availability.TIMEOUT => "timeout",
                Availability.ERROR => "error",
                _ when latencyMs >= 0 => $"{latencyMs} ms",
                _ => "-"
            };
        }

        private string FormatStatus(GoidaNodeStatus status)
        {
            return status switch
            {
                GoidaNodeStatus.Ok => Localize("Lang.Goida.Status.Ok"),
                GoidaNodeStatus.Timeout => Localize("Lang.Goida.Status.Timeout"),
                GoidaNodeStatus.Error => Localize("Lang.Goida.Status.Error"),
                _ => Localize("Lang.Goida.Status.Unknown")
            };
        }

        private void OnNodesUpdated()
        {
            Dispatcher.UIThread.Post(RefreshList);
        }

        private void OnSettingsChanged(object? sender, RoutedEventArgs e)
        {
            CaptureSettingsFromControls();
        }

        private void OnSelectionModeChanged(object? sender, SelectionChangedEventArgs e)
        {
            CaptureSettingsFromControls();
        }

        private void OnFilterChanged(object? sender, TextChangedEventArgs e)
        {
            RefreshList();
            UpdateStatusSummary();
        }

        private async void OnRefreshClick(object? sender, RoutedEventArgs e)
        {
            if (goidaHandler == null)
                return;

            SetStatusText(Localize("Lang.Goida.Loading"));
            try
            {
                await goidaHandler.Manager.RefreshListsAsync().ConfigureAwait(true);
                RefreshList();
                UpdateStatusSummary();
            }
            catch
            {
                SetStatusText(Localize("Lang.Goida.RefreshFailed"));
            }
        }

        private async void OnProbeClick(object? sender, RoutedEventArgs e)
        {
            if (goidaHandler == null)
                return;

            int? listFilter = GetActiveListFilter();
            SetStatusText(listFilter is int listId
                ? string.Format(Localize("Lang.Goida.ProbingList"), listId)
                : Localize("Lang.Goida.Probing"));
            try
            {
                await goidaHandler.Manager.ProbeAsync(manual: true, listIdFilter: listFilter).ConfigureAwait(true);
                RefreshList();
                UpdateStatusSummary();
            }
            catch
            {
                SetStatusText(Localize("Lang.Goida.ProbeFailed"));
            }
        }

        private void OnNodeSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
        }

        private NodeRow? GetSelectedRow()
        {
            return listNodes.SelectedItem as NodeRow;
        }

        private void OnSetActiveClick(object? sender, RoutedEventArgs e)
        {
            NodeRow? row = GetSelectedRow();
            if (row == null || goidaHandler == null || getUserSettings == null || onUpdateUserSettings == null)
                return;

            UserSettings current = getUserSettings();
            GoidaProfileSettings settings = current.GetGoidaSettings().Clone();
            settings.Enabled = true;
            settings.ActiveNodeId = row.Id;
            settings.PinnedNodeId = row.Id;
            settings.SelectionMode = GoidaSelectionMode.ManualFixed;
            current.Goida = settings;
            onUpdateUserSettings(current);

            GoidaNode? node = goidaHandler.Manager.GetNodesSorted()
                .FirstOrDefault(candidate => string.Equals(candidate.Id, row.Id, StringComparison.OrdinalIgnoreCase));
            if (node != null)
                onActiveNodeChanged?.Invoke(node);

            ApplySettingsToControls();
            RefreshList();
        }

        private void OnAddToPoolClick(object? sender, RoutedEventArgs e)
        {
            NodeRow? row = GetSelectedRow();
            if (row == null || getUserSettings == null || onUpdateUserSettings == null)
                return;

            UserSettings current = getUserSettings();
            GoidaProfileSettings settings = current.GetGoidaSettings().Clone();
            settings.ManualPoolNodeIds ??= new List<string>();
            if (!settings.ManualPoolNodeIds.Contains(row.Id, StringComparer.OrdinalIgnoreCase))
                settings.ManualPoolNodeIds.Add(row.Id);
            settings.SelectionMode = GoidaSelectionMode.ManualPool;
            current.Goida = settings;
            onUpdateUserSettings(current);
            goidaHandler?.Manager.UpdateSettings(settings);
            ApplySettingsToControls();
            RefreshList();
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private string Localize(string key)
        {
            return LocalizationService.GetTerm(key);
        }
    }
}
