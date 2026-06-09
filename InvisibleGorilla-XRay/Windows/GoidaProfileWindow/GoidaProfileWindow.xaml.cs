using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using InvisibleGorillaXRay.Core;
using InvisibleGorillaXRay.Handlers;
using InvisibleGorillaXRay.Models;
using InvisibleGorillaXRay.Services;
using InvisibleGorillaXRay.Services.Goida;
using InvisibleGorillaXRay.Values;

namespace InvisibleGorillaXRay
{
    public partial class GoidaProfileWindow : Window
    {
        private sealed class NodeRow
        {
            public int ListId { get; init; }
            public string Id { get; init; } = string.Empty;
            public string DisplayName { get; init; } = string.Empty;
            public string Country { get; init; } = string.Empty;
            public string Protocol { get; init; } = string.Empty;
            public string Endpoint { get; init; } = string.Empty;
            public string LatencyText { get; init; } = string.Empty;
            public string StatusText { get; init; } = string.Empty;
            public string LastCheckedText { get; init; } = string.Empty;
            public bool IsActive { get; init; }
            public string ActiveMark => IsActive ? "✓" : string.Empty;
            public bool InPool { get; init; }
        }

        private enum SortMode
        {
            ByLatency,
            ByStatus,
            ByList,
            ByCountry,
            ByName
        }

        private GoidaProfileHandler? goidaHandler;
        private Func<UserSettings>? getUserSettings;
        private Action<UserSettings>? onUpdateUserSettings;
        private Action<GoidaNode>? onActiveNodeChanged;
        private bool isApplyingSettings;
        private bool initialLoadStarted;
        private bool isApplyingListSelection;
        private bool probeUiInProgress;
        private CancellationTokenSource? probeCts;
        private DateTime lastGridRefreshUtc = DateTime.MinValue;
        private int probeCurrent;
        private int probeTotal;
        private readonly Dictionary<int, CheckBox> listCheckboxes = new();
        private DispatcherTimer? listSelectionSaveTimer;
        private bool listSelectionDirty;
        private string? pendingActiveNodeId;
        private const int MaxDisplayRows = 500;

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

            // Guard the whole init: populating combo boxes fires SelectionChanged ->
            // OnSettingsChanged synchronously, which would otherwise save default
            // control values (auto-switch off, mode AutoBest) over the user's settings.
            isApplyingSettings = true;
            try
            {
                PopulateSelectionModes();
                PopulateSortModes();
                BuildListCheckboxes();
                InitializeListSelectionTimer();
            }
            finally
            {
                isApplyingSettings = false;
            }

            ApplySettingsToControls();
            ApplyListSelectionToControls();
            // Pending selection starts empty: it is only set when the user explicitly
            // picks a node, otherwise CONFIRM must not touch the active node or mode.
            pendingActiveNodeId = null;
            RefreshGrid();
            UpdateStatusSummary();
            UpdatePoolInfo();

            goidaHandler.Manager.NodesUpdated += OnNodesUpdated;
            goidaHandler.Manager.ProbeProgress += OnProbeProgress;
            goidaHandler.Manager.StatusMessage += OnStatusMessage;
            Loaded += OnWindowLoaded;
            Closed += (_, __) =>
            {
                probeCts?.Cancel();
                probeCts?.Dispose();
                listSelectionSaveTimer?.Stop();
                if (listSelectionDirty)
                    PersistListSelectionNow();
                goidaHandler.Manager.NodesUpdated -= OnNodesUpdated;
                goidaHandler.Manager.ProbeProgress -= OnProbeProgress;
                goidaHandler.Manager.StatusMessage -= OnStatusMessage;
            };
        }

        private void OnProbeProgress(GoidaProbeProgress progress)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                probeCurrent = progress.Current;
                probeTotal = progress.Total;
                progressProbe.Maximum = Math.Max(1, progress.Total);
                progressProbe.Value = progress.Current;

                string nodeName = progress.Node?.DisplayName ?? "-";
                SetStatusText(string.Format(
                    Localize("Lang.Goida.ProbingProgress"),
                    progress.Current,
                    progress.Total,
                    nodeName,
                    FormatLatency(progress.LatencyMs),
                    FormatStatus(progress.Status)));

                RefreshGridThrottled(force: progress.Current == progress.Total);
            }));
        }

        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
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

            if (!HasVpnListsSelected())
            {
                SetStatusText(Localize("Lang.Goida.NoListsSelected"));
                return;
            }

            if (goidaHandler.Manager.GetVisibleNodes().Count > 0)
            {
                RefreshGrid();
                UpdateStatusSummary();
                return;
            }

            await RefreshListsOnlyAsync().ConfigureAwait(true);
        }

        private bool HasVpnListsSelected()
        {
            if (getUserSettings == null)
                return false;

            return GoidaProfileManager.HasVpnListsEnabled(getUserSettings().GetGoidaSettings());
        }

        private async Task RefreshListsOnlyAsync()
        {
            if (goidaHandler == null)
                return;

            if (!HasVpnListsSelected())
            {
                SetStatusText(Localize("Lang.Goida.NoListsSelected"));
                return;
            }

            SetStatusText(Localize("Lang.Goida.Loading"));
            try
            {
                await goidaHandler.Manager.RefreshListsAsync().ConfigureAwait(true);
                RefreshGrid();
                UpdateStatusSummary();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("GoidaProfileWindow.RefreshListsOnly", ex);
                SetStatusText(Localize("Lang.Goida.RefreshFailed"));
            }
        }

        private void OnStatusMessage(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (string.Equals(message, "refresh-failed", StringComparison.OrdinalIgnoreCase))
                    SetStatusText(Localize("Lang.Goida.RefreshFailed"));
                else if (string.Equals(message, "refresh-no-vpn-lists", StringComparison.OrdinalIgnoreCase))
                    SetStatusText(Localize("Lang.Goida.NoListsSelected"));
                else if (string.Equals(message, "verify-start", StringComparison.OrdinalIgnoreCase))
                    SetStatusText(Localize("Lang.Goida.VerifyingTop"));
                else if (string.Equals(message, "verify-complete", StringComparison.OrdinalIgnoreCase))
                {
                    RefreshGrid(forceLatencySort: true);
                    UpdateStatusSummary();
                }
                else if (string.Equals(message, "refresh-complete", StringComparison.OrdinalIgnoreCase))
                {
                    RefreshGrid();
                    UpdateStatusSummary();
                }
            }));
        }

        private void SetStatusText(string text)
        {
            textBlockStatus.Text = text ?? string.Empty;
        }

        private void UpdateStatusSummary()
        {
            if (goidaHandler == null)
                return;

            if (!HasVpnListsSelected())
            {
                SetStatusText(Localize("Lang.Goida.NoListsSelected"));
                return;
            }

            IReadOnlyList<GoidaNode> nodes = goidaHandler.Manager.GetVisibleNodes();
            int nodeCount = nodes.Count > 0
                ? nodes.Count
                : goidaHandler.Manager.CountVisibleNodes();
            if (nodeCount == 0)
            {
                SetStatusText(Localize("Lang.Goida.EmptyHint"));
                return;
            }

            (int ok, int timeout, int error, int unknown) = goidaHandler.Manager.GetProbeSummary();
            string activeText = BuildActiveStatusText();

            SetStatusText(string.Format(
                Localize("Lang.Goida.ProbeSummary"),
                nodeCount,
                ok,
                timeout,
                error,
                unknown,
                activeText));
        }

        private string BuildActiveStatusText()
        {
            if (goidaHandler == null || getUserSettings == null)
                return Localize("Lang.Goida.NoActiveNode");

            GoidaProfileSettings settings = getUserSettings().GetGoidaSettings();
            string effectiveId = !string.IsNullOrWhiteSpace(pendingActiveNodeId)
                ? pendingActiveNodeId
                : settings.ActiveNodeId;

            if (string.IsNullOrWhiteSpace(effectiveId))
                return Localize("Lang.Goida.NoActiveNode");

            GoidaNode? node = goidaHandler.Manager.GetNodesSorted()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, effectiveId, StringComparison.OrdinalIgnoreCase));

            if (node == null)
                return Localize("Lang.Goida.NoActiveNode");

            string summary = string.Format(
                Localize("Lang.Goida.ActiveSummary"),
                node.DisplayName,
                FormatLatency(node.LatencyMs));

            bool pending = !string.IsNullOrWhiteSpace(pendingActiveNodeId)
                && !string.Equals(pendingActiveNodeId, settings.ActiveNodeId, StringComparison.OrdinalIgnoreCase);

            return pending
                ? summary + " · " + Localize("Lang.Goida.PendingApply")
                : summary;
        }

        private async Task RefreshAndProbeAsync()
        {
            await RefreshListsOnlyAsync().ConfigureAwait(true);
            await ProbeVisibleNodesAsync().ConfigureAwait(true);
        }

        private async Task ProbeVisibleNodesAsync()
        {
            if (goidaHandler == null || probeUiInProgress)
                return;

            if (goidaHandler.Manager.CountManualProbeTargets() == 0)
            {
                SetStatusText(Localize("Lang.Goida.EmptyHint"));
                return;
            }

            probeCts?.Cancel();
            probeCts?.Dispose();
            probeCts = new CancellationTokenSource();

            probeUiInProgress = true;
            probeCurrent = 0;
            probeTotal = Math.Max(1, goidaHandler.Manager.CountManualProbeTargets());
            BeginProbeUi();
            try
            {
                GoidaProbeResult result = await goidaHandler.Manager
                    .ProbeAsync(probeCts.Token, manual: true)
                    .ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    RefreshGrid(forceLatencySort: true);
                    ShowProbeCompleteResult(result);
                });
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.InvokeAsync(() =>
                    SetStatusText(string.Format(
                        Localize("Lang.Goida.ProbeCancelled"),
                        probeCurrent,
                        probeTotal)));
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("GoidaProfileWindow.ProbeVisibleNodes", ex);
                await Dispatcher.InvokeAsync(() => SetStatusText(Localize("Lang.Goida.ProbeFailed")));
            }
            finally
            {
                await Dispatcher.InvokeAsync(EndProbeUi);
                probeCts?.Dispose();
                probeCts = null;
            }
        }

        private void BeginProbeUi()
        {
            buttonRefreshLists.IsEnabled = false;
            buttonProbeAll.Content = Localize("Lang.Goida.CancelProbe");
            progressProbe.Visibility = Visibility.Visible;
            progressProbe.Value = 0;
            progressProbe.Maximum = Math.Max(1, probeTotal);
            SetStatusText(Localize("Lang.Goida.Probing"));
        }

        private void EndProbeUi()
        {
            probeUiInProgress = false;
            buttonRefreshLists.IsEnabled = true;
            buttonProbeAll.Content = Localize("Lang.Goida.ProbeAll");
            progressProbe.Visibility = Visibility.Collapsed;
        }

        private void ShowProbeCompleteResult(GoidaProbeResult result)
        {
            if (result.Cancelled)
                return;

            if (result.Total == 0)
            {
                SetStatusText(goidaHandler?.Manager.CountManualProbeTargets() == 0
                    ? Localize("Lang.Goida.EmptyHint")
                    : Localize("Lang.Goida.ProbeBusy"));
                UpdateStatusSummary();
                return;
            }

            string bestText = result.BestNode == null
                ? Localize("Lang.Goida.NoWorkingNode")
                : string.Format(
                    Localize("Lang.Goida.BestNodeSummary"),
                    result.BestNode.DisplayName,
                    result.BestNode.LatencyMs);

            SetStatusText(string.Format(
                Localize("Lang.Goida.ProbeComplete"),
                result.Completed,
                result.Ok,
                result.Timeout,
                result.Error,
                bestText));

            UpdateStatusSummary();
        }

        private void RefreshGridThrottled(bool force = false)
        {
            if (!force && probeUiInProgress
                && (DateTime.UtcNow - lastGridRefreshUtc).TotalMilliseconds < 350)
            {
                return;
            }

            lastGridRefreshUtc = DateTime.UtcNow;
            RefreshGrid(forceLatencySort: probeUiInProgress);
        }

        private void BuildListCheckboxes()
        {
            panelListCheckboxes.Children.Clear();
            listCheckboxes.Clear();

            if (goidaHandler == null)
                return;

            foreach (GoidaListMeta list in goidaHandler.Manager.Lists)
            {
                CheckBox box = new CheckBox
                {
                    Content = list.Id.ToString(),
                    Tag = list.Id,
                    Foreground = System.Windows.Media.Brushes.White,
                    Margin = new Thickness(0, 0, 8, 4),
                    MinWidth = 34,
                    ToolTip = list.Title
                };
                box.Click += OnListCheckboxClick;
                listCheckboxes[list.Id] = box;
                panelListCheckboxes.Children.Add(box);
            }
        }

        private void ApplyListSelectionToControls()
        {
            if (getUserSettings == null)
                return;

            isApplyingListSelection = true;
            try
            {
                HashSet<int> enabled = getUserSettings().GetGoidaSettings().EnabledListIds?.ToHashSet()
                    ?? Enumerable.Range(1, 25).ToHashSet();
                foreach (KeyValuePair<int, CheckBox> pair in listCheckboxes)
                    pair.Value.IsChecked = enabled.Contains(pair.Key);
            }
            finally
            {
                isApplyingListSelection = false;
            }
        }

        private void InitializeListSelectionTimer()
        {
            listSelectionSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            listSelectionSaveTimer.Tick += (_, __) =>
            {
                listSelectionSaveTimer?.Stop();
                PersistListSelectionNow();
            };
        }

        private void OnListCheckboxClick(object sender, RoutedEventArgs e)
        {
            if (isApplyingListSelection)
                return;

            listSelectionDirty = true;
            listSelectionSaveTimer?.Stop();
            listSelectionSaveTimer?.Start();
            UpdateStatusSummary();

            if (!HasVpnListsSelected())
                SetStatusText(Localize("Lang.Goida.NoListsSelected"));
        }

        private void PersistListSelectionNow()
        {
            if (!listSelectionDirty || getUserSettings == null || onUpdateUserSettings == null)
                return;

            List<int> selected = listCheckboxes
                .Where(pair => pair.Value.IsChecked == true)
                .Select(pair => pair.Key)
                .OrderBy(id => id)
                .ToList();

            UserSettings current = getUserSettings();
            GoidaProfileSettings settings = current.GetGoidaSettings().Clone();
            settings.EnabledListIds = selected;
            current.Goida = settings;
            onUpdateUserSettings(current);
            listSelectionDirty = false;

            Dispatcher.BeginInvoke(new Action(() => RefreshGrid()), DispatcherPriority.Background);
        }

        private void CaptureListSelectionFromControls()
        {
            if (isApplyingListSelection)
                return;

            listSelectionDirty = true;
            listSelectionSaveTimer?.Stop();
            PersistListSelectionNow();
        }

        private void SetListSelection(IEnumerable<int> listIds)
        {
            HashSet<int> selected = listIds.ToHashSet();
            isApplyingListSelection = true;
            try
            {
                foreach (KeyValuePair<int, CheckBox> pair in listCheckboxes)
                    pair.Value.IsChecked = selected.Contains(pair.Key);
            }
            finally
            {
                isApplyingListSelection = false;
            }

            CaptureListSelectionFromControls();
        }

        private void OnSelectListsNodesClick(object sender, RoutedEventArgs e)
        {
            SetListSelection(Enumerable.Range(1, 25));
        }

        private void OnSelectListsWhitelistClick(object sender, RoutedEventArgs e)
        {
            SetListSelection(new[] { 26 });
            SetStatusText(Localize("Lang.Goida.List26Selected"));
        }

        private void OnSelectListsAllClick(object sender, RoutedEventArgs e)
        {
            SetListSelection(Enumerable.Range(1, 26));
        }

        private void OnSelectListsNoneClick(object sender, RoutedEventArgs e)
        {
            SetListSelection(new[] { 1 });
        }

        private void PopulateSelectionModes()
        {
            comboBoxSelectionMode.ItemsSource = new[]
            {
                new ComboBoxItem { Content = Localize("Lang.Goida.Mode.AutoBest"), Tag = GoidaSelectionMode.AutoBest },
                new ComboBoxItem { Content = Localize("Lang.Goida.Mode.ManualFixed"), Tag = GoidaSelectionMode.ManualFixed },
                new ComboBoxItem { Content = Localize("Lang.Goida.Mode.ManualPool"), Tag = GoidaSelectionMode.ManualPool }
            };
            comboBoxSelectionMode.SelectedIndex = 0;
        }

        private void PopulateSortModes()
        {
            comboBoxSortMode.ItemsSource = new[]
            {
                new ComboBoxItem { Content = Localize("Lang.Goida.Sort.Latency"), Tag = SortMode.ByLatency },
                new ComboBoxItem { Content = Localize("Lang.Goida.Sort.Status"), Tag = SortMode.ByStatus },
                new ComboBoxItem { Content = Localize("Lang.Goida.Sort.List"), Tag = SortMode.ByList },
                new ComboBoxItem { Content = Localize("Lang.Goida.Sort.Country"), Tag = SortMode.ByCountry },
                new ComboBoxItem { Content = Localize("Lang.Goida.Sort.Name"), Tag = SortMode.ByName }
            };
            comboBoxSortMode.SelectedIndex = 0;
        }

        private SortMode GetSelectedSortMode()
        {
            if (comboBoxSortMode.SelectedItem is ComboBoxItem item && item.Tag is SortMode mode)
                return mode;

            return SortMode.ByLatency;
        }

        private IEnumerable<GoidaNode> ApplySort(IEnumerable<GoidaNode> nodes, SortMode sortMode)
        {
            return ApplySortStatic(nodes, sortMode);
        }

        private void ApplySettingsToControls()
        {
            if (getUserSettings == null)
                return;

            isApplyingSettings = true;
            try
            {
                GoidaProfileSettings settings = getUserSettings().GetGoidaSettings();
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

        private void RefreshGrid(bool forceLatencySort = false)
        {
            if (goidaHandler == null || getUserSettings == null)
                return;

            GoidaProfileSettings settings = getUserSettings().GetGoidaSettings();
            string filter = textBoxFilterList.Text?.Trim() ?? string.Empty;
            int? listFilter = int.TryParse(filter, out int listId) ? listId : null;

            List<GoidaNode> visible = goidaHandler.Manager.GetVisibleNodes()
                .Where(node => listFilter == null || node.ListId == listFilter)
                .ToList();

            SortMode sortMode = forceLatencySort || probeUiInProgress
                ? SortMode.ByLatency
                : GetSelectedSortMode();

            List<GoidaNode> filtered = BuildDisplayNodes(visible, sortMode);

            bool truncated = visible.Count > MaxDisplayRows;
            HashSet<string> pool = settings.ManualPoolNodeIds?
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<NodeRow> rows = filtered
                .Select(node => ToNodeRow(node, settings, pool))
                .ToList();

            gridNodes.ItemsSource = rows;

            if (truncated && !probeUiInProgress)
            {
                SetStatusText(string.Format(
                    Localize("Lang.Goida.ShowingLimited"),
                    rows.Count,
                    visible.Count));
            }
        }

        private static List<GoidaNode> BuildDisplayNodes(List<GoidaNode> visible, SortMode sortMode)
        {
            if (visible.Count <= MaxDisplayRows)
                return ApplySortStatic(visible, sortMode).ToList();

            List<GoidaNode> checkedNodes = visible
                .Where(node => node.LastCheckedUtc != default)
                .ToList();
            List<GoidaNode> uncheckedNodes = visible
                .Where(node => node.LastCheckedUtc == default)
                .ToList();

            List<GoidaNode> orderedChecked = ApplySortStatic(checkedNodes, sortMode).ToList();
            int remaining = Math.Max(0, MaxDisplayRows - orderedChecked.Count);
            return orderedChecked
                .Concat(uncheckedNodes.Take(remaining))
                .Take(MaxDisplayRows)
                .ToList();
        }

        private static IEnumerable<GoidaNode> ApplySortStatic(IEnumerable<GoidaNode> nodes, SortMode sortMode)
        {
            return sortMode switch
            {
                SortMode.ByStatus => nodes
                    .OrderBy(node => node.Status == GoidaNodeStatus.Ok ? 0 : node.Status == GoidaNodeStatus.Unknown ? 1 : 2)
                    .ThenBy(node => node.LatencyMs < 0 ? int.MaxValue : node.LatencyMs)
                    .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase),
                SortMode.ByList => nodes
                    .OrderBy(node => node.ListId)
                    .ThenBy(node => node.LatencyMs < 0 ? int.MaxValue : node.LatencyMs)
                    .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase),
                SortMode.ByCountry => nodes
                    .OrderBy(node => node.Country, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(node => node.LatencyMs < 0 ? int.MaxValue : node.LatencyMs)
                    .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase),
                SortMode.ByName => nodes
                    .OrderBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(node => node.ListId),
                _ => nodes
                    .OrderBy(node => node.Status == GoidaNodeStatus.Ok ? 0 : 1)
                    .ThenBy(node => node.LatencyMs < 0 ? int.MaxValue : node.LatencyMs)
                    .ThenBy(node => node.ListId)
                    .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
            };
        }

        private NodeRow ToNodeRow(GoidaNode node, GoidaProfileSettings settings, HashSet<string> pool)
        {
            string activeId = !string.IsNullOrWhiteSpace(pendingActiveNodeId)
                ? pendingActiveNodeId
                : settings.ActiveNodeId;

            return new NodeRow
            {
                ListId = node.ListId,
                Id = node.Id,
                DisplayName = node.DisplayName,
                Country = GoidaNodeDisplay.FormatCountryDisplay(node.Country),
                Protocol = string.IsNullOrWhiteSpace(node.Protocol) ? "-" : node.Protocol,
                Endpoint = node.Endpoint,
                LatencyText = FormatLatency(node.LatencyMs),
                StatusText = FormatStatus(node.Status),
                LastCheckedText = GoidaNodeDisplay.FormatLastChecked(node.LastCheckedUtc),
                IsActive = string.Equals(node.Id, activeId, StringComparison.OrdinalIgnoreCase),
                InPool = pool.Contains(node.Id)
            };
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

        private static string FormatStatus(GoidaNodeStatus status)
        {
            return status switch
            {
                GoidaNodeStatus.Ok => "OK",
                GoidaNodeStatus.Timeout => "Timeout",
                GoidaNodeStatus.Error => "Error",
                _ => "Unknown"
            };
        }

        private void OnSortModeChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshGrid();
        }

        private void OnNodesUpdated()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshGridThrottled();
                if (!probeUiInProgress)
                    UpdateStatusSummary();
            }));
        }

        private void OnSettingsChanged(object sender, RoutedEventArgs e)
        {
            CaptureSettingsFromControls();
            UpdatePoolInfo();
        }

        private void OnFilterChanged(object sender, TextChangedEventArgs e)
        {
            RefreshGrid();
        }

        private async void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            if (goidaHandler == null)
                return;

            await RefreshListsOnlyAsync().ConfigureAwait(true);
        }

        private async void OnProbeClick(object sender, RoutedEventArgs e)
        {
            if (goidaHandler == null)
                return;

            if (probeUiInProgress)
            {
                probeCts?.Cancel();
                return;
            }

            try
            {
                await ProbeVisibleNodesAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("GoidaProfileWindow.OnProbeClick", ex);
                SetStatusText(Localize("Lang.Goida.ProbeFailed"));
            }
        }

        private void OnNodeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private NodeRow? GetSelectedRow()
        {
            return gridNodes.SelectedItem as NodeRow;
        }

        private List<NodeRow> GetSelectedRows()
        {
            return gridNodes.SelectedItems.OfType<NodeRow>().ToList();
        }

        private void OnUseBestClick(object sender, RoutedEventArgs e)
        {
            if (goidaHandler == null || getUserSettings == null)
                return;

            GoidaProfileSettings settings = getUserSettings().GetGoidaSettings().Clone();
            // In fixed mode "best" would always return the pinned node, which makes
            // the button useless — evaluate as auto-best instead.
            if (settings.SelectionMode == GoidaSelectionMode.ManualFixed)
                settings.SelectionMode = GoidaSelectionMode.AutoBest;

            GoidaNode? best = GoidaActiveSelector.SelectBestNode(
                settings,
                goidaHandler.Manager.GetVisibleNodes(),
                settings.ActiveNodeId);

            if (best == null)
            {
                SetStatusText(Localize("Lang.Goida.NoWorkingNode"));
                return;
            }

            pendingActiveNodeId = best.Id;
            RefreshGrid();
            SetStatusText(string.Format(Localize("Lang.Goida.PendingActive"), best.DisplayName));
        }

        private void OnSetActiveClick(object sender, RoutedEventArgs e)
        {
            NodeRow? row = GetSelectedRow();
            if (row == null || string.IsNullOrWhiteSpace(row.Id))
                return;

            pendingActiveNodeId = row.Id;
            RefreshGrid();
            SetStatusText(string.Format(Localize("Lang.Goida.PendingActive"), row.DisplayName));
        }

        private void OnPoolCheckBoxClick(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox box || box.Tag is not string nodeId || string.IsNullOrWhiteSpace(nodeId))
                return;

            ModifyPool(pool =>
            {
                if (box.IsChecked == true)
                {
                    if (!pool.Contains(nodeId, StringComparer.OrdinalIgnoreCase))
                        pool.Add(nodeId);
                }
                else
                {
                    pool.RemoveAll(id => string.Equals(id, nodeId, StringComparison.OrdinalIgnoreCase));
                }
            });
        }

        private void OnAddToPoolClick(object sender, RoutedEventArgs e)
        {
            List<NodeRow> rows = GetSelectedRows();
            if (rows.Count == 0)
                return;

            ModifyPool(pool =>
            {
                foreach (NodeRow row in rows)
                {
                    if (!pool.Contains(row.Id, StringComparer.OrdinalIgnoreCase))
                        pool.Add(row.Id);
                }
            });
        }

        private void OnClearPoolClick(object sender, RoutedEventArgs e)
        {
            ModifyPool(pool => pool.Clear());
        }

        private void ModifyPool(Action<List<string>> mutate)
        {
            if (getUserSettings == null || onUpdateUserSettings == null)
                return;

            UserSettings current = getUserSettings();
            GoidaProfileSettings settings = current.GetGoidaSettings().Clone();
            settings.ManualPoolNodeIds ??= new List<string>();
            mutate(settings.ManualPoolNodeIds);
            current.Goida = settings;
            onUpdateUserSettings(current);
            goidaHandler?.Manager.UpdateSettings(settings);
            RefreshGrid();
            UpdatePoolInfo();
        }

        private void UpdatePoolInfo()
        {
            if (getUserSettings == null)
                return;

            GoidaProfileSettings settings = getUserSettings().GetGoidaSettings();
            int count = settings.ManualPoolNodeIds?.Count ?? 0;
            string text = string.Format(Localize("Lang.Goida.PoolCount"), count);

            if (count > 0 && settings.SelectionMode != GoidaSelectionMode.ManualPool)
                text += " · " + Localize("Lang.Goida.PoolModeHint");

            textBlockPoolInfo.Text = text;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            if (listSelectionDirty)
                PersistListSelectionNow();

            CaptureSettingsFromControls();

            bool hasNewActive = !string.IsNullOrWhiteSpace(pendingActiveNodeId)
                && goidaHandler != null
                && getUserSettings != null
                && onUpdateUserSettings != null
                && !string.Equals(
                    pendingActiveNodeId,
                    getUserSettings().GetGoidaSettings().ActiveNodeId,
                    StringComparison.OrdinalIgnoreCase);

            if (hasNewActive)
            {
                UserSettings current = getUserSettings();
                GoidaProfileSettings settings = current.GetGoidaSettings().Clone();
                settings.ActiveNodeId = pendingActiveNodeId;
                // Pin only in fixed mode; auto/pool modes keep their own selection logic
                // and the user's mode choice must never be silently overwritten.
                if (settings.SelectionMode == GoidaSelectionMode.ManualFixed)
                    settings.PinnedNodeId = pendingActiveNodeId;
                current.Goida = settings;
                onUpdateUserSettings(current);
                goidaHandler.Manager.UpdateSettings(settings);
                goidaHandler.Manager.SetActiveNode(pendingActiveNodeId, notifyExternal: true);
                GoidaNode? node = goidaHandler.Manager.GetActiveNode();
                if (node != null)
                {
                    onActiveNodeChanged?.Invoke(node);
                    if (Application.Current.MainWindow is MainWindow mainWindow
                        && GoidaProfilePaths.IsMarker(getUserSettings()?.GetCurrentConfigPath())
                        && mainWindow.IsServerRunning)
                    {
                        mainWindow.TryRerun();
                    }
                }
            }

            DialogResult = true;
            Close();
        }

        private string Localize(string key)
        {
            return TryFindResource(key)?.ToString() ?? key;
        }
    }
}
