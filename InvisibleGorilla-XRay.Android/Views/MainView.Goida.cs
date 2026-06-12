using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using InvisibleGorillaXRay.Core;
using InvisibleGorillaXRay.Models;
using InvisibleGorillaXRay.Services.Goida;
using InvisibleGorillaXRay.Values;

namespace InvisibleGorillaXRay.Android.Views
{
    public partial class MainView
    {
        private enum GoidaSortMode
        {
            ByLatency,
            ByStatus,
            ByList,
            ByCountry,
            ByName
        }

        private sealed class GoidaNodeRow
        {
            public int ListId { get; init; }
            public string Id { get; init; } = string.Empty;
            public string DisplayName { get; init; } = string.Empty;
            public string Endpoint { get; init; } = string.Empty;
            public string LatencyText { get; init; } = string.Empty;
            public string MetaLine { get; init; } = string.Empty;
            public string ActiveMark { get; init; } = string.Empty;
            public string PoolLabel { get; init; } = string.Empty;
            public bool InPool { get; init; }
        }

        private bool isApplyingGoidaSettings;
        private bool isApplyingGoidaListSelection;
        private bool isGoidaSectionInitialized;
        private bool goidaProbeUiInProgress;
        private bool goidaListSelectionDirty;
        private bool isRefreshingGoidaNodesList;
        private string? goidaPendingActiveNodeId;
        private CancellationTokenSource? goidaProbeCts;
        private int goidaProbeCurrent;
        private int goidaProbeTotal;
        private DateTime goidaLastGridRefreshUtc = DateTime.MinValue;
        private int suppressGoidaConnectionRestart;
        private readonly Dictionary<int, CheckBox> goidaListCheckboxes = new();

        private const int GoidaMaxDisplayRows = 200;

        private StackPanel GoidaSectionScroll => GetRequiredControl<StackPanel>("GoidaSectionPanel");
        private Button GoidaNavButton => GetRequiredControl<Button>("GoidaSectionButton");

        private void OnGoidaSectionClick(object? sender, RoutedEventArgs e)
        {
            // The Goida section opens on demand and initializes itself lazily, so it must never
            // be gated behind an early-return: doing so previously left the nav button visually
            // present but dead when Setup had not flipped isInitialized for any reason.
            if (goidaHandler == null)
            {
                pendingGoidaSectionOpen = true;
                return;
            }

            OpenGoidaSection();
        }

        private void OpenGoidaSection()
        {
            try
            {
                ShowSection(NavigationSection.Goida);
                EnsureGoidaSectionInitialized();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.Goida.OpenSection", ex);
                SetStatus(ex.Message);
            }
        }

        private void EnsureGoidaSectionInitialized()
        {
            RebindGoidaControls();
            localizationHandler.MergeInto(this);
            ApplyGoidaLocalizedText();

            if (!isGoidaSectionInitialized)
            {
                BuildGoidaListCheckboxes();
                isGoidaSectionInitialized = true;
            }

            ApplyGoidaSettingsToControls();
            ApplyGoidaListSelectionToControls();
            RefreshGoidaNodesListBox();
            UpdateGoidaPoolInfo();
            UpdateGoidaStatusSummary();
        }

        private void InitializeGoidaControls()
        {
            RebindGoidaControls();
            goidaHandler.Manager.ProbeProgress += OnGoidaProbeProgress;
            goidaHandler.Manager.StatusMessage += OnGoidaStatusMessage;
            goidaPendingActiveNodeId = null;
            ApplyGoidaComboBoxItems();
        }

        // On Android the Avalonia NameGenerator x:Name fields for the Goida section are not reliably
        // populated at runtime (they can stay null), which previously made Setup throw a
        // NullReferenceException and left the whole view half-initialized. Resolving them on demand
        // via FindControl is the supported workaround used elsewhere in this view.
        private void RebindGoidaControls()
        {
            GoidaSelectionModeComboBox = this.FindControl<ComboBox>("GoidaSelectionModeComboBox") ?? GoidaSelectionModeComboBox;
            GoidaSortModeComboBox = this.FindControl<ComboBox>("GoidaSortModeComboBox") ?? GoidaSortModeComboBox;
            GoidaEnabledCheckBox = this.FindControl<CheckBox>("GoidaEnabledCheckBox") ?? GoidaEnabledCheckBox;
            GoidaAutoSwitchCheckBox = this.FindControl<CheckBox>("GoidaAutoSwitchCheckBox") ?? GoidaAutoSwitchCheckBox;
            GoidaFilterListTextBox = this.FindControl<TextBox>("GoidaFilterListTextBox") ?? GoidaFilterListTextBox;
            GoidaListCheckboxesPanel = this.FindControl<WrapPanel>("GoidaListCheckboxesPanel") ?? GoidaListCheckboxesPanel;
            GoidaNodesListBox = this.FindControl<ListBox>("GoidaNodesListBox") ?? GoidaNodesListBox;
            GoidaRefreshButton = this.FindControl<Button>("GoidaRefreshButton") ?? GoidaRefreshButton;
            GoidaProbeButton = this.FindControl<Button>("GoidaProbeButton") ?? GoidaProbeButton;
            GoidaProbeProgressBar = this.FindControl<ProgressBar>("GoidaProbeProgressBar") ?? GoidaProbeProgressBar;
            GoidaStatusTextBlock = this.FindControl<TextBlock>("GoidaStatusTextBlock") ?? GoidaStatusTextBlock;
            GoidaPoolInfoTextBlock = this.FindControl<TextBlock>("GoidaPoolInfoTextBlock") ?? GoidaPoolInfoTextBlock;
            GoidaActiveSummaryTextBlock = this.FindControl<TextBlock>("GoidaActiveSummaryTextBlock") ?? GoidaActiveSummaryTextBlock;
        }

        private void ApplyGoidaLocalizedText()
        {
            ApplyGoidaComboBoxItems();
        }

        private void ApplyGoidaComboBoxItems()
        {
            int selectionModeIndex = GoidaSelectionModeComboBox.SelectedIndex;
            int sortModeIndex = GoidaSortModeComboBox.SelectedIndex;

            isApplyingGoidaSettings = true;
            try
            {
            GoidaSelectionModeComboBox.ItemsSource = new[]
            {
                Localize("Lang.Goida.Mode.AutoBest"),
                Localize("Lang.Goida.Mode.ManualFixed"),
                Localize("Lang.Goida.Mode.ManualPool")
            };

            GoidaSortModeComboBox.ItemsSource = new[]
            {
                Localize("Lang.Goida.Sort.Latency"),
                Localize("Lang.Goida.Sort.Status"),
                Localize("Lang.Goida.Sort.List"),
                Localize("Lang.Goida.Sort.Country"),
                Localize("Lang.Goida.Sort.Name")
            };

            if (selectionModeIndex >= 0 && selectionModeIndex < 3)
                GoidaSelectionModeComboBox.SelectedIndex = selectionModeIndex;
            else if (GoidaSelectionModeComboBox.SelectedIndex < 0)
                GoidaSelectionModeComboBox.SelectedIndex = 0;

            if (sortModeIndex >= 0 && sortModeIndex < 5)
                GoidaSortModeComboBox.SelectedIndex = sortModeIndex;
            else if (GoidaSortModeComboBox.SelectedIndex < 0)
                GoidaSortModeComboBox.SelectedIndex = 0;
            }
            finally
            {
                isApplyingGoidaSettings = false;
            }
        }

        private void SetGoidaProbeButtonLabel(string text)
        {
            GoidaProbeButton.Content = text;
            GoidaProbeButton.Foreground = Brushes.White;
        }

        private void BuildGoidaListCheckboxes()
        {
            GoidaListCheckboxesPanel.Children.Clear();
            goidaListCheckboxes.Clear();

            foreach (GoidaListMeta list in goidaHandler.Manager.Lists)
            {
                CheckBox box = new CheckBox
                {
                    Content = list.Id.ToString(),
                    Tag = list.Id,
                    Foreground = Brushes.White,
                    Margin = new Thickness(0, 0, 8, 4),
                    MinWidth = 34
                };
                box.Click += OnGoidaListCheckboxClick;
                goidaListCheckboxes[list.Id] = box;
                GoidaListCheckboxesPanel.Children.Add(box);
            }
        }

        private void ApplyGoidaSettingsToControls()
        {
            isApplyingGoidaSettings = true;
            try
            {
                GoidaProfileSettings settings = settingsHandler.UserSettings.GetGoidaSettings();
                GoidaEnabledCheckBox.IsChecked = settings.Enabled;
                GoidaAutoSwitchCheckBox.IsChecked = settings.AutoSwitchOnFly;
                GoidaSelectionModeComboBox.SelectedIndex = settings.SelectionMode switch
                {
                    GoidaSelectionMode.ManualFixed => 1,
                    GoidaSelectionMode.ManualPool => 2,
                    _ => 0
                };
            }
            finally
            {
                isApplyingGoidaSettings = false;
            }
        }

        private void ApplyGoidaListSelectionToControls()
        {
            isApplyingGoidaListSelection = true;
            try
            {
                HashSet<int> enabled = settingsHandler.UserSettings.GetGoidaSettings().EnabledListIds?
                    .ToHashSet() ?? Enumerable.Range(1, 25).ToHashSet();
                foreach (KeyValuePair<int, CheckBox> pair in goidaListCheckboxes)
                    pair.Value.IsChecked = enabled.Contains(pair.Key);
            }
            finally
            {
                isApplyingGoidaListSelection = false;
            }
        }

        private void CaptureGoidaSettingsFromControls()
        {
            if (!isInitialized || isApplyingGoidaSettings)
                return;

            try
            {
                UserSettings current = settingsHandler.UserSettings;
                GoidaProfileSettings settings = current.GetGoidaSettings().Clone();
                settings.Enabled = GoidaEnabledCheckBox.IsChecked == true;
                settings.AutoSwitchOnFly = GoidaAutoSwitchCheckBox.IsChecked == true;
                settings.SelectionMode = GoidaSelectionModeComboBox.SelectedIndex switch
                {
                    1 => GoidaSelectionMode.ManualFixed,
                    2 => GoidaSelectionMode.ManualPool,
                    _ => GoidaSelectionMode.AutoBest
                };

                current.Goida = settings;
                settingsHandler.UpdateUserSettings(current);
                goidaHandler.Manager.UpdateSettings(settings);
                SafeRefreshGoidaSummary();
                UpdateGoidaPoolInfo();
                SafeUpdateGoidaStatusSummary();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.Goida.CaptureSettings", ex);
            }
        }

        private void PersistGoidaListSelection()
        {
            if (!goidaListSelectionDirty)
                return;

            List<int> selected = goidaListCheckboxes
                .Where(pair => pair.Value.IsChecked == true)
                .Select(pair => pair.Key)
                .OrderBy(id => id)
                .ToList();

            UserSettings current = settingsHandler.UserSettings;
            GoidaProfileSettings settings = current.GetGoidaSettings().Clone();
            settings.EnabledListIds = selected;
            current.Goida = settings;
            settingsHandler.UpdateUserSettings(current);
            goidaListSelectionDirty = false;
        }

        private void CaptureGoidaListSelectionFromControls()
        {
            if (isApplyingGoidaListSelection)
                return;

            goidaListSelectionDirty = true;
            PersistGoidaListSelection();
        }

        private void SetGoidaListSelection(IEnumerable<int> listIds)
        {
            HashSet<int> selected = listIds.ToHashSet();
            isApplyingGoidaListSelection = true;
            try
            {
                foreach (KeyValuePair<int, CheckBox> pair in goidaListCheckboxes)
                    pair.Value.IsChecked = selected.Contains(pair.Key);
            }
            finally
            {
                isApplyingGoidaListSelection = false;
            }

            CaptureGoidaListSelectionFromControls();
            RefreshGoidaNodesListBox();
            UpdateGoidaStatusSummary();
        }

        private void RefreshGoidaSummary()
        {
            GoidaNode? activeNode = goidaHandler.Manager.GetActiveNode();
            Border banner = GetRequiredControl<Border>("GoidaSignalBanner");

            if (activeNode == null)
            {
                GoidaActiveSummaryTextBlock.Text = string.Empty;
                banner.IsVisible = false;
                return;
            }

            string latency = activeNode.LatencyMs >= 0
                ? $"{activeNode.LatencyMs} ms"
                : "-";
            GoidaActiveSummaryTextBlock.Text = LocalizeFormat(
                "Lang.Goida.ActiveSummary",
                activeNode.DisplayName,
                latency);

            GoidaMainPresentation presentation =
                GoidaNodeDisplay.BuildMainPresentation(activeNode);

            IBrush statusBrush = TryParseBrush(presentation.ColorHex, "#6DCC8E");
            IBrush inactiveBrush = TryParseBrush("#4A4A4A", "#4A4A4A");
            int level = presentation.SignalLevel;

            GetRequiredControl<Avalonia.Controls.Shapes.Ellipse>("GoidaWifiDot").Fill = statusBrush;
            GetRequiredControl<Avalonia.Controls.Shapes.Path>("GoidaWifiArcInner").Stroke =
                level >= 2 ? statusBrush : inactiveBrush;
            GetRequiredControl<Avalonia.Controls.Shapes.Path>("GoidaWifiArcMiddle").Stroke =
                level >= 3 ? statusBrush : inactiveBrush;
            GetRequiredControl<Avalonia.Controls.Shapes.Path>("GoidaWifiArcOuter").Stroke =
                level >= 4 ? statusBrush : inactiveBrush;

            TextBlock signalLabel = GetRequiredControl<TextBlock>("GoidaSignalLabelTextBlock");
            signalLabel.Text = Localize(presentation.QualityLabel);
            signalLabel.Foreground = statusBrush;
            GetRequiredControl<TextBlock>("GoidaSignalLatencyTextBlock").Text = presentation.LatencyText;
            banner.IsVisible = true;
        }

        private void RefreshGoidaNodesListBox(bool forceLatencySort = false)
        {
            isRefreshingGoidaNodesList = true;
            try
            {
                RefreshGoidaNodesListBoxCore(forceLatencySort);
            }
            finally
            {
                isRefreshingGoidaNodesList = false;
            }
        }

        private void RefreshGoidaNodesListBoxCore(bool forceLatencySort = false)
        {
            GoidaProfileSettings settings = settingsHandler.UserSettings.GetGoidaSettings();
            string filter = GoidaFilterListTextBox.Text?.Trim() ?? string.Empty;
            int? listFilter = int.TryParse(filter, out int listId) ? listId : null;

            List<GoidaNode> visible = goidaHandler.Manager.GetVisibleNodes()
                .Where(node => listFilter == null || node.ListId == listFilter)
                .ToList();

            GoidaSortMode sortMode = forceLatencySort || goidaProbeUiInProgress
                ? GoidaSortMode.ByLatency
                : GetSelectedGoidaSortMode();

            List<GoidaNode> display = BuildGoidaDisplayNodes(visible, sortMode);
            bool truncated = visible.Count > GoidaMaxDisplayRows;
            HashSet<string> pool = settings.ManualPoolNodeIds?
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string effectiveActiveId = !string.IsNullOrWhiteSpace(goidaPendingActiveNodeId)
                ? goidaPendingActiveNodeId!
                : settings.ActiveNodeId;

            List<GoidaNodeRow> rows = display
                .Select(node => ToGoidaNodeRow(node, effectiveActiveId, pool))
                .ToList();

            GoidaNodesListBox.ItemsSource = rows;

            if (truncated && !goidaProbeUiInProgress)
                SetGoidaStatusTextBlock(LocalizeFormat(
                    "Lang.Goida.ShowingLimited",
                    rows.Count,
                    visible.Count));
        }

        private static List<GoidaNode> BuildGoidaDisplayNodes(List<GoidaNode> visible, GoidaSortMode sortMode)
        {
            if (visible.Count <= GoidaMaxDisplayRows)
                return ApplyGoidaSort(visible, sortMode).ToList();

            List<GoidaNode> checkedNodes = visible
                .Where(node => node.LastCheckedUtc != default)
                .ToList();
            List<GoidaNode> uncheckedNodes = visible
                .Where(node => node.LastCheckedUtc == default)
                .ToList();

            List<GoidaNode> orderedChecked = ApplyGoidaSort(checkedNodes, sortMode).ToList();
            int remaining = Math.Max(0, GoidaMaxDisplayRows - orderedChecked.Count);
            return orderedChecked
                .Concat(uncheckedNodes.Take(remaining))
                .Take(GoidaMaxDisplayRows)
                .ToList();
        }

        private static IEnumerable<GoidaNode> ApplyGoidaSort(IEnumerable<GoidaNode> nodes, GoidaSortMode sortMode)
        {
            return sortMode switch
            {
                GoidaSortMode.ByStatus => nodes
                    .OrderBy(node => node.Status == GoidaNodeStatus.Ok ? 0 : node.Status == GoidaNodeStatus.Unknown ? 1 : 2)
                    .ThenBy(node => node.LatencyMs < 0 ? int.MaxValue : node.LatencyMs)
                    .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase),
                GoidaSortMode.ByList => nodes
                    .OrderBy(node => node.ListId)
                    .ThenBy(node => node.LatencyMs < 0 ? int.MaxValue : node.LatencyMs)
                    .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase),
                GoidaSortMode.ByCountry => nodes
                    .OrderBy(node => node.Country, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(node => node.LatencyMs < 0 ? int.MaxValue : node.LatencyMs)
                    .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase),
                GoidaSortMode.ByName => nodes
                    .OrderBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(node => node.ListId),
                _ => nodes
                    .OrderBy(node => node.Status == GoidaNodeStatus.Ok ? 0 : 1)
                    .ThenBy(node => node.LatencyMs < 0 ? int.MaxValue : node.LatencyMs)
                    .ThenBy(node => node.ListId)
                    .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
            };
        }

        private GoidaNodeRow ToGoidaNodeRow(GoidaNode node, string activeId, HashSet<string> pool)
        {
            string country = string.IsNullOrWhiteSpace(node.Country)
                ? GoidaNodeDisplay.ExtractCountry(node.DisplayName, node.Endpoint)
                : node.Country;
            string protocol = string.IsNullOrWhiteSpace(node.Protocol) ? "-" : node.Protocol;
            return new GoidaNodeRow
            {
                ListId = node.ListId,
                Id = node.Id,
                DisplayName = node.DisplayName,
                Endpoint = node.Endpoint,
                LatencyText = FormatGoidaLatency(node.LatencyMs),
                MetaLine = $"{country} · {protocol} · {FormatGoidaStatus(node)}",
                ActiveMark = string.Equals(node.Id, activeId, StringComparison.OrdinalIgnoreCase) ? "✓" : string.Empty,
                PoolLabel = Localize("Lang.Goida.AddToPool"),
                InPool = pool.Contains(node.Id)
            };
        }

        private GoidaSortMode GetSelectedGoidaSortMode()
        {
            return GoidaSortModeComboBox.SelectedIndex switch
            {
                1 => GoidaSortMode.ByStatus,
                2 => GoidaSortMode.ByList,
                3 => GoidaSortMode.ByCountry,
                4 => GoidaSortMode.ByName,
                _ => GoidaSortMode.ByLatency
            };
        }

        private static string FormatGoidaLatency(int latencyMs)
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

        private string FormatGoidaStatus(GoidaNode node)
        {
            if (node.VlessVerified && node.Status == GoidaNodeStatus.Ok)
                return Localize("Lang.Goida.Status.VlessOk");

            if (node.LatencyMs >= 0 && node.Status != GoidaNodeStatus.Error
                && node.Status != GoidaNodeStatus.Timeout)
                return Localize("Lang.Goida.Status.TcpOnly");

            return node.Status switch
            {
                GoidaNodeStatus.Timeout => Localize("Lang.Goida.Status.Timeout"),
                GoidaNodeStatus.Error => Localize("Lang.Goida.Status.Error"),
                _ => Localize("Lang.Goida.Status.Unknown")
            };
        }

        private GoidaNodeRow? GetSelectedGoidaRow() => GoidaNodesListBox.SelectedItem as GoidaNodeRow;

        private bool IsGoidaConnectionRestartSuppressed => suppressGoidaConnectionRestart > 0;

        private void WithGoidaConnectionRestartSuppressed(Action action)
        {
            suppressGoidaConnectionRestart++;
            try
            {
                action();
            }
            finally
            {
                suppressGoidaConnectionRestart--;
            }
        }

        private void HandleGoidaActiveNodeChanged(GoidaNode node)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.ConfigPath))
                return;

            if (!settingsHandler.UserSettings.GetGoidaSettings().Enabled)
                return;

            settingsHandler.UpdateCurrentConfigPath(GoidaProfilePaths.MarkerPath);

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    UpdateCurrentConfigSummary();
                    if (!IsGoidaConnectionRestartSuppressed && IsConnectionActive())
                        _ = RestartConnectionAfterSettingsChangeAsync();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteException("MainView.Goida.ActiveNodeChanged", ex);
                }
            });
        }

        private void OnGoidaNodesUpdated()
        {
            Dispatcher.UIThread.Post(ApplyGoidaNodesUpdatedUi);
        }

        private void ApplyGoidaNodesUpdatedUi()
        {
            if (!isInitialized)
                return;

            try
            {
                if (GoidaSectionScroll.IsVisible || isGoidaSectionInitialized)
                {
                    RefreshGoidaNodesListBoxThrottled();
                    if (!goidaProbeUiInProgress)
                    {
                        SafeRefreshGoidaSummary();
                        SafeUpdateGoidaStatusSummary();
                    }
                }

                UpdateCurrentConfigSummary();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.Goida.NodesUpdated", ex);
            }
        }

        private void SafeRefreshGoidaSummary()
        {
            try
            {
                RefreshGoidaSummary();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.Goida.RefreshSummary", ex);
            }
        }

        private void SafeUpdateGoidaStatusSummary()
        {
            try
            {
                UpdateGoidaStatusSummary();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.Goida.StatusSummary", ex);
            }
        }

        private void RefreshGoidaNodesListBoxThrottled(bool force = false)
        {
            if (!force && goidaProbeUiInProgress
                && (DateTime.UtcNow - goidaLastGridRefreshUtc).TotalMilliseconds < 350)
                return;

            goidaLastGridRefreshUtc = DateTime.UtcNow;
            RefreshGoidaNodesListBox(forceLatencySort: goidaProbeUiInProgress);
        }

        private void OnGoidaProbeProgress(GoidaProbeProgress progress)
        {
            Dispatcher.UIThread.Post(() =>
            {
                goidaProbeCurrent = progress.Current;
                goidaProbeTotal = progress.Total;
                GoidaProbeProgressBar.Maximum = Math.Max(1, progress.Total);
                GoidaProbeProgressBar.Value = progress.Current;

                string nodeName = progress.Node?.DisplayName ?? "-";
                SetGoidaStatusTextBlock(LocalizeFormat(
                    "Lang.Goida.ProbingProgress",
                    progress.Current,
                    progress.Total,
                    nodeName,
                    FormatGoidaLatency(progress.LatencyMs),
                    progress.Node != null
                        ? FormatGoidaStatus(progress.Node)
                        : FormatProbeStatus(progress.Status)));

                RefreshGoidaNodesListBoxThrottled(force: progress.Current == progress.Total);
            });
        }

        private static string FormatProbeStatus(GoidaNodeStatus status)
        {
            return status switch
            {
                GoidaNodeStatus.Ok => "OK",
                GoidaNodeStatus.Timeout => "Timeout",
                GoidaNodeStatus.Error => "Error",
                _ => "Unknown"
            };
        }

        private void OnGoidaStatusMessage(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (string.Equals(message, "refresh-failed", StringComparison.OrdinalIgnoreCase))
                    SetGoidaStatusTextBlock(Localize("Lang.Goida.RefreshFailed"));
                else if (string.Equals(message, "refresh-complete", StringComparison.OrdinalIgnoreCase))
                {
                    RefreshGoidaNodesListBox();
                    UpdateGoidaStatusSummary();
                }
            });
        }

        private void SetGoidaStatusTextBlock(string text) => GoidaStatusTextBlock.Text = text ?? string.Empty;

        private void UpdateGoidaStatusSummary()
        {
            if (!GoidaProfileManager.HasVpnListsEnabled(settingsHandler.UserSettings.GetGoidaSettings()))
            {
                SetGoidaStatusTextBlock(Localize("Lang.Goida.NoListsSelected"));
                return;
            }

            IReadOnlyList<GoidaNode> nodes = goidaHandler.Manager.GetVisibleNodes();
            int nodeCount = nodes.Count > 0
                ? nodes.Count
                : goidaHandler.Manager.CountVisibleNodes();

            if (nodeCount == 0)
            {
                SetGoidaStatusTextBlock(Localize("Lang.Goida.EmptyHint"));
                return;
            }

            (int ok, int timeout, int error, int unknown) = goidaHandler.Manager.GetProbeSummary();
            SetGoidaStatusTextBlock(LocalizeFormat(
                "Lang.Goida.ProbeSummary",
                nodeCount,
                ok,
                timeout,
                error,
                unknown,
                BuildGoidaActiveStatusText()));
        }

        private string BuildGoidaActiveStatusText()
        {
            GoidaProfileSettings settings = settingsHandler.UserSettings.GetGoidaSettings();
            string effectiveId = !string.IsNullOrWhiteSpace(goidaPendingActiveNodeId)
                ? goidaPendingActiveNodeId!
                : settings.ActiveNodeId;

            if (string.IsNullOrWhiteSpace(effectiveId))
                return Localize("Lang.Goida.NoActiveNode");

            GoidaNode? node = goidaHandler.Manager.GetNodesSorted()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, effectiveId, StringComparison.OrdinalIgnoreCase));

            if (node == null)
                return Localize("Lang.Goida.NoActiveNode");

            string summary = LocalizeFormat(
                "Lang.Goida.ActiveSummary",
                node.DisplayName,
                FormatGoidaLatency(node.LatencyMs));

            bool pending = !string.IsNullOrWhiteSpace(goidaPendingActiveNodeId)
                && !string.Equals(goidaPendingActiveNodeId, settings.ActiveNodeId, StringComparison.OrdinalIgnoreCase);

            return pending
                ? summary + " · " + Localize("Lang.Goida.PendingApply")
                : summary;
        }

        private void UpdateGoidaPoolInfo()
        {
            GoidaProfileSettings settings = settingsHandler.UserSettings.GetGoidaSettings();
            int count = settings.ManualPoolNodeIds?.Count ?? 0;
            string text = LocalizeFormat("Lang.Goida.PoolCount", count);

            if (count > 0 && settings.SelectionMode != GoidaSelectionMode.ManualPool)
                text += " · " + Localize("Lang.Goida.PoolModeHint");

            GoidaPoolInfoTextBlock.Text = text;
        }

        private void OnGoidaSettingsChanged(object? sender, RoutedEventArgs e) => CaptureGoidaSettingsFromControls();

        private void OnGoidaSelectionModeChanged(object? sender, SelectionChangedEventArgs e) => CaptureGoidaSettingsFromControls();

        private void OnGoidaSortModeChanged(object? sender, SelectionChangedEventArgs e) => RefreshGoidaNodesListBox();

        private void OnGoidaFilterChanged(object? sender, TextChangedEventArgs e) => RefreshGoidaNodesListBox();

        private void OnGoidaListCheckboxClick(object? sender, RoutedEventArgs e)
        {
            if (!isInitialized || isApplyingGoidaListSelection)
                return;

            try
            {
                CaptureGoidaListSelectionFromControls();
                RefreshGoidaNodesListBox();
                UpdateGoidaStatusSummary();

                if (!GoidaProfileManager.HasVpnListsEnabled(settingsHandler.UserSettings.GetGoidaSettings()))
                    SetGoidaStatusTextBlock(Localize("Lang.Goida.NoListsSelected"));
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.Goida.ListCheckbox", ex);
            }
        }

        private void OnGoidaSelectListsNodesClick(object? sender, RoutedEventArgs e) =>
            SetGoidaListSelection(Enumerable.Range(1, 25));

        private void OnGoidaSelectListsWhitelistClick(object? sender, RoutedEventArgs e)
        {
            SetGoidaListSelection(new[] { GoidaWhitelistStore.ListId });
            SetGoidaStatusTextBlock(Localize("Lang.Goida.List26Selected"));
        }

        private void OnGoidaSelectListsAllClick(object? sender, RoutedEventArgs e) =>
            SetGoidaListSelection(Enumerable.Range(1, 26));

        private void OnGoidaSelectListsNoneClick(object? sender, RoutedEventArgs e) =>
            SetGoidaListSelection(new[] { 1 });

        private async void OnGoidaRefreshClick(object? sender, RoutedEventArgs e)
        {
            CaptureGoidaListSelectionFromControls();

            if (!GoidaProfileManager.HasVpnListsEnabled(settingsHandler.UserSettings.GetGoidaSettings()))
            {
                SetGoidaStatusTextBlock(Localize("Lang.Goida.NoListsSelected"));
                return;
            }

            SetGoidaStatusTextBlock(Localize("Lang.Goida.Loading"));
            try
            {
                await goidaHandler.Manager.RefreshListsAsync();
                RefreshGoidaNodesListBox();
                UpdateGoidaStatusSummary();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.Goida.Refresh", ex);
                SetGoidaStatusTextBlock(Localize("Lang.Goida.RefreshFailed"));
            }
        }

        private async void OnGoidaProbeClick(object? sender, RoutedEventArgs e)
        {
            if (goidaProbeUiInProgress)
            {
                goidaProbeCts?.Cancel();
                return;
            }

            CaptureGoidaListSelectionFromControls();

            int visibleCount = goidaHandler.Manager.GetVisibleNodes().Count;
            if (goidaHandler.Manager.CountManualProbeTargets() == 0)
            {
                SetGoidaStatusTextBlock(visibleCount > 0
                    ? LocalizeFormat("Lang.Goida.ProbeNoTargets", visibleCount)
                    : Localize("Lang.Goida.EmptyHint"));
                return;
            }

            goidaProbeCts?.Cancel();
            goidaProbeCts?.Dispose();
            goidaProbeCts = new CancellationTokenSource();

            goidaProbeUiInProgress = true;
            goidaProbeCurrent = 0;
            goidaProbeTotal = Math.Max(1, goidaHandler.Manager.CountManualProbeTargets());
            BeginGoidaProbeUi();

            try
            {
                GoidaProbeResult result = await goidaHandler.Manager
                    .ProbeAsync(goidaProbeCts.Token, manual: true);

                RefreshGoidaNodesListBox(forceLatencySort: true);
                ShowGoidaProbeCompleteResult(result);
            }
            catch (OperationCanceledException)
            {
                SetGoidaStatusTextBlock(LocalizeFormat(
                    "Lang.Goida.ProbeCancelled",
                    goidaProbeCurrent,
                    goidaProbeTotal));
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.Goida.Probe", ex);
                SetGoidaStatusTextBlock(Localize("Lang.Goida.ProbeFailed"));
            }
            finally
            {
                EndGoidaProbeUi();
                goidaProbeCts?.Dispose();
                goidaProbeCts = null;
            }
        }

        private void BeginGoidaProbeUi()
        {
            GoidaRefreshButton.IsEnabled = false;
            SetGoidaProbeButtonLabel(Localize("Lang.Goida.CancelProbe"));
            GoidaProbeProgressBar.IsVisible = true;
            GoidaProbeProgressBar.Value = 0;
            GoidaProbeProgressBar.Maximum = Math.Max(1, goidaProbeTotal);
            SetGoidaStatusTextBlock(Localize("Lang.Goida.Probing"));
        }

        private void EndGoidaProbeUi()
        {
            goidaProbeUiInProgress = false;
            GoidaRefreshButton.IsEnabled = true;
            SetGoidaProbeButtonLabel(Localize("Lang.Goida.ProbeAll"));
            GoidaProbeProgressBar.IsVisible = false;
        }

        private void ShowGoidaProbeCompleteResult(GoidaProbeResult result)
        {
            if (result.Cancelled)
                return;

            if (result.Total == 0)
            {
                SetGoidaStatusTextBlock(goidaHandler.Manager.CountManualProbeTargets() == 0
                    ? Localize("Lang.Goida.EmptyHint")
                    : Localize("Lang.Goida.ProbeBusy"));
                UpdateGoidaStatusSummary();
                return;
            }

            string bestText = result.BestNode == null
                ? Localize("Lang.Goida.NoWorkingNode")
                : LocalizeFormat(
                    "Lang.Goida.BestNodeSummary",
                    result.BestNode.DisplayName,
                    result.BestNode.LatencyMs);

            SetGoidaStatusTextBlock(LocalizeFormat(
                "Lang.Goida.ProbeComplete",
                result.Completed,
                result.Ok,
                result.Timeout,
                result.Error,
                bestText));

            UpdateGoidaStatusSummary();
        }

        private void OnGoidaNodeSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
        }

        private void OnGoidaSetActiveClick(object? sender, RoutedEventArgs e)
        {
            GoidaNodeRow? row = GetSelectedGoidaRow();
            if (row == null)
                return;

            goidaPendingActiveNodeId = row.Id;
            RefreshGoidaNodesListBox();
            SetGoidaStatusTextBlock(LocalizeFormat("Lang.Goida.PendingActive", row.DisplayName));
        }

        private void OnGoidaUseBestClick(object? sender, RoutedEventArgs e)
        {
            GoidaProfileSettings settings = settingsHandler.UserSettings.GetGoidaSettings().Clone();
            if (settings.SelectionMode == GoidaSelectionMode.ManualFixed)
                settings.SelectionMode = GoidaSelectionMode.AutoBest;

            List<GoidaNode> visible = goidaHandler.Manager.GetVisibleNodes()
                .Where(node => node.VlessVerified
                    && node.Status == GoidaNodeStatus.Ok
                    && node.LatencyMs >= 0)
                .ToList();

            GoidaNode? best = visible.Count > 0
                ? GoidaActiveSelector.SelectBestNode(settings, visible, settings.ActiveNodeId)
                : null;

            if (best == null)
            {
                SetGoidaStatusTextBlock(Localize("Lang.Goida.NoWorkingNode"));
                return;
            }

            goidaPendingActiveNodeId = best.Id;
            RefreshGoidaNodesListBox();
            SetGoidaStatusTextBlock(LocalizeFormat("Lang.Goida.PendingActive", best.DisplayName));
        }

        private void OnGoidaAutoPoolClick(object? sender, RoutedEventArgs e)
        {
            List<GoidaNode> top = goidaHandler.Manager.GetVisibleNodes()
                .Where(node => node.VlessVerified
                    && node.Status == GoidaNodeStatus.Ok
                    && node.LatencyMs >= 0)
                .OrderBy(node => node.LatencyMs)
                .Take(GoidaProfileSettings.DefaultAutoPoolSize)
                .ToList();

            if (top.Count == 0)
            {
                SetGoidaStatusTextBlock(Localize("Lang.Goida.AutoPoolNeedsVerify"));
                return;
            }

            UserSettings current = settingsHandler.UserSettings;
            GoidaProfileSettings settings = current.GetGoidaSettings().Clone();
            settings.ManualPoolNodeIds = top.Select(node => node.Id).ToList();

            GoidaNodeRow? selected = GetSelectedGoidaRow();
            if (selected != null
                && !string.IsNullOrWhiteSpace(selected.Id)
                && !settings.ManualPoolNodeIds.Contains(selected.Id, StringComparer.OrdinalIgnoreCase))
                settings.ManualPoolNodeIds.Add(selected.Id);

            settings.SelectionMode = GoidaSelectionMode.ManualPool;
            settings.AutoSwitchOnFly = true;
            settings.Enabled = true;
            current.Goida = settings;
            settingsHandler.UpdateUserSettings(current);
            goidaHandler.Manager.UpdateSettings(settings);

            ApplyGoidaSettingsToControls();
            RefreshGoidaNodesListBox();
            UpdateGoidaPoolInfo();
            SetGoidaStatusTextBlock(LocalizeFormat(
                "Lang.Goida.AutoPoolBuilt",
                settings.ManualPoolNodeIds.Count));
        }

        private void OnGoidaClearPoolClick(object? sender, RoutedEventArgs e) =>
            ModifyGoidaPool(pool => pool.Clear());

        private static IBrush TryParseBrush(string? colorHex, string fallbackHex)
        {
            try
            {
                return new SolidColorBrush(Color.Parse(string.IsNullOrWhiteSpace(colorHex) ? fallbackHex : colorHex));
            }
            catch
            {
                return new SolidColorBrush(Color.Parse(fallbackHex));
            }
        }

        private void OnGoidaPoolCheckBoxChanged(object? sender, RoutedEventArgs e)
        {
            if (isRefreshingGoidaNodesList)
                return;

            if (sender is not CheckBox box || box.Tag is not string nodeId || string.IsNullOrWhiteSpace(nodeId))
                return;

            ModifyGoidaPool(pool =>
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

        private void ModifyGoidaPool(Action<List<string>> mutate)
        {
            UserSettings current = settingsHandler.UserSettings;
            GoidaProfileSettings settings = current.GetGoidaSettings().Clone();
            settings.ManualPoolNodeIds ??= new List<string>();
            mutate(settings.ManualPoolNodeIds);
            current.Goida = settings;
            settingsHandler.UpdateUserSettings(current);
            goidaHandler.Manager.UpdateSettings(settings);
            RefreshGoidaNodesListBox();
            UpdateGoidaPoolInfo();
        }

        private void OnGoidaApplyClick(object? sender, RoutedEventArgs e)
        {
            if (goidaListSelectionDirty)
                PersistGoidaListSelection();

            CaptureGoidaSettingsFromControls();

            if (!string.IsNullOrWhiteSpace(goidaPendingActiveNodeId)
                && !string.Equals(
                    goidaPendingActiveNodeId,
                    settingsHandler.UserSettings.GetGoidaSettings().ActiveNodeId,
                    StringComparison.OrdinalIgnoreCase))
            {
                UserSettings current = settingsHandler.UserSettings;
                GoidaProfileSettings settings = current.GetGoidaSettings().Clone();
                settings.Enabled = true;
                settings.ActiveNodeId = goidaPendingActiveNodeId!;
                if (settings.SelectionMode == GoidaSelectionMode.ManualFixed)
                    settings.PinnedNodeId = goidaPendingActiveNodeId!;
                current.Goida = settings;
                settingsHandler.UpdateUserSettings(current);
                goidaHandler.Manager.UpdateSettings(settings);
                goidaHandler.Manager.SetActiveNode(goidaPendingActiveNodeId!);
                goidaPendingActiveNodeId = null;
            }

            ApplyGoidaSettingsToControls();
            RefreshGoidaNodesListBox();
            RefreshGoidaSummary();
            UpdateGoidaStatusSummary();
            SetGoidaStatusTextBlock(Localize("Lang.Goida.ConfirmHint"));
        }
    }
}
