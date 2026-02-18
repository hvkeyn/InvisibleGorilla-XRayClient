using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace InvisibleGorillaXRay.Mac.Views
{
    using Models;
    using Values;
    using Services;
    using Utilities;
    using Services.Analytics.ServerWindow;
    using Services.Analytics.Configuration;

    public partial class ServerWindow : Window
    {
        private enum ImportingType { FILE, LINK }
        private enum SubscriptionOperation { CREATE, EDIT }

        private string configPath;
        private string groupPath;
        private ImportingType importingType;
        private SubscriptionOperation subscriptionOperation;

        private Func<string> getCurrentConfigPath;
        private Func<bool> isCurrentPathEqualRootConfigPath;
        private Func<List<Config>> getAllGeneralConfigs;
        private Func<string, List<Config>> getAllSubscriptionConfigs;
        private Func<List<Subscription>> getAllGroups;
        private Func<string, Status> convertLinkToConfig;
        private Func<string, string, Status> convertLinkToSubscription;
        private Func<string, Status> loadConfig;
        private Func<string, int> testConnection;
        private Func<string> getLogPath;
        private Action<string> onCopyConfig;
        private Action<string, string> onCreateConfig;
        private Action<string, string, string> onCreateSubscription;
        private Action<Subscription> onDeleteSubscription;
        private Action<GroupType, string> onDeleteConfig;
        private Action<string> onUpdateConfig;

        private AnalyticsService AnalyticsService => ServiceLocator.Get<AnalyticsService>();
        private LocalizationService LocalizationService => ServiceLocator.Get<LocalizationService>();

        public ServerWindow()
        {
            InitializeComponent();
            SetActiveFileImportingGroup(true);
            SetActiveLinkImportingGroup(false);
            importingType = ImportingType.FILE;

            Opened += OnWindowOpened;
        }

        public void Setup(
            Func<string> getCurrentConfigPath,
            Func<bool> isCurrentPathEqualRootConfigPath,
            Func<List<Config>> getAllGeneralConfigs,
            Func<string, List<Config>> getAllSubscriptionConfigs,
            Func<List<Subscription>> getAllGroups,
            Func<string, Status> convertLinkToConfig,
            Func<string, string, Status> convertLinkToSubscription,
            Func<string, Status> loadConfig,
            Func<string, int> testConnection,
            Func<string> getLogPath,
            Action<string> onCopyConfig,
            Action<string, string> onCreateConfig,
            Action<string, string, string> onCreateSubscription,
            Action<Subscription> onDeleteSubscription,
            Action<GroupType, string> onDeleteConfig,
            Action<string> onUpdateConfig)
        {
            this.getCurrentConfigPath = getCurrentConfigPath;
            this.isCurrentPathEqualRootConfigPath = isCurrentPathEqualRootConfigPath;
            this.getAllGeneralConfigs = getAllGeneralConfigs;
            this.getAllSubscriptionConfigs = getAllSubscriptionConfigs;
            this.getAllGroups = getAllGroups;
            this.convertLinkToConfig = convertLinkToConfig;
            this.convertLinkToSubscription = convertLinkToSubscription;
            this.loadConfig = loadConfig;
            this.testConnection = testConnection;
            this.getLogPath = getLogPath;
            this.onCopyConfig = onCopyConfig;
            this.onCreateConfig = onCreateConfig;
            this.onCreateSubscription = onCreateSubscription;
            this.onDeleteSubscription = onDeleteSubscription;
            this.onDeleteConfig = onDeleteConfig;
            this.onUpdateConfig = onUpdateConfig;
        }

        private void OnWindowOpened(object sender, EventArgs e)
        {
            groupPath = getCurrentConfigPath?.Invoke();
            LoadGroupsList();
            LoadConfigsList(GroupType.GENERAL);
            LoadConfigsList(GroupType.SUBSCRIPTION);
            ShowServersPanel();

            if (isCurrentPathEqualRootConfigPath?.Invoke() == true)
                OnConfigTabClick(null, null);
            else
                OnSubscriptionTabClick(null, null);
        }

        private void OnConfigTabClick(object sender, RoutedEventArgs e)
        {
            EnableAllTabs();
            HideAllPanels();
            buttonConfigTab.IsEnabled = false;
            panelConfig.IsVisible = true;
        }

        private void OnSubscriptionTabClick(object sender, RoutedEventArgs e)
        {
            EnableAllTabs();
            HideAllPanels();
            buttonSubscriptionTab.IsEnabled = false;
            panelSubscription.IsVisible = true;
        }

        private void OnAddConfigButtonClick(object sender, RoutedEventArgs e)
        {
            panelServers.IsVisible = false;
            panelAddConfigs.IsVisible = true;
            AnalyticsService.SendEvent(new AddConfigButtonClickedEvent());
        }

        private void OnAddSubscriptionButtonClick(object sender, RoutedEventArgs e)
        {
            subscriptionOperation = SubscriptionOperation.CREATE;
            textBoxSubscriptionRemarks.Text = "";
            textBoxSubscriptionLink.Text = "";
            panelServers.IsVisible = false;
            panelAddSubscriptions.IsVisible = true;
            AnalyticsService.SendEvent(new AddSubButtonClickedEvent());
        }

        private void OnFileRadioButtonClick(object sender, RoutedEventArgs e)
        {
            SetActiveFileImportingGroup(true);
            SetActiveLinkImportingGroup(false);
            importingType = ImportingType.FILE;
        }

        private void OnLinkRadioButtonClick(object sender, RoutedEventArgs e)
        {
            SetActiveFileImportingGroup(false);
            SetActiveLinkImportingGroup(true);
            importingType = ImportingType.LINK;
        }

        private async void OnChooseFileButtonClick(object sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add configuration",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Config files") { Patterns = new[] { "*.json", "*.toml", "*.yaml", "*.yml" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
                }
            });

            if (files.Count == 0) return;

            var file = files[0];
            textBlockFileName.Text = file.Name;
            configPath = file.Path.LocalPath;
        }

        private void OnImportConfigButtonClick(object sender, RoutedEventArgs e)
        {
            if (importingType == ImportingType.FILE)
            {
                if (string.IsNullOrEmpty(configPath))
                    return;

                panelLoading.IsVisible = true;
                TryAddConfigFromFile();
                AnalyticsService.SendEvent(new ConfigFromFileImportedEvent());
            }
            else
            {
                if (string.IsNullOrEmpty(textBoxConfigLink.Text))
                    return;

                panelLoading.IsVisible = true;
                TryAddConfigFromLink();
                AnalyticsService.SendEvent(new ConfigFromLinkImportedEvent());
            }
        }

        private void TryAddConfigFromFile()
        {
            Status configStatus = loadConfig.Invoke(configPath);
            if (configStatus.Code == Code.ERROR)
            {
                panelLoading.IsVisible = false;
                return;
            }

            onCopyConfig.Invoke(configPath);
            FinishAddConfig();
        }

        private void TryAddConfigFromLink()
        {
            Status configStatus = convertLinkToConfig.Invoke(textBoxConfigLink.Text);
            if (configStatus.Code == Code.ERROR)
            {
                panelLoading.IsVisible = false;
                return;
            }

            string[] config = (string[])configStatus.Content;
            onCreateConfig.Invoke(config[0], config[1]);
            FinishAddConfig();
        }

        private void FinishAddConfig()
        {
            string lastPath = GetLastConfigPath(GroupType.GENERAL);
            groupPath = lastPath;
            onUpdateConfig.Invoke(lastPath);
            panelLoading.IsVisible = false;
            LoadConfigsList(GroupType.GENERAL);
            configPath = null;
            textBlockFileName.Text = "No file chosen";
            textBoxConfigLink.Text = "";
            ShowServersPanel();
        }

        private void OnImportSubscriptionButtonClick(object sender, RoutedEventArgs e)
        {
            if (subscriptionOperation == SubscriptionOperation.CREATE)
            {
                HandleImportingSubscription();
                AnalyticsService.SendEvent(new SubFromLinkImportedEvent());
            }
            else
            {
                HandleEditSubscription();
                AnalyticsService.SendEvent(new SubFromLinkEditedEvent());
            }
        }

        private void HandleImportingSubscription()
        {
            if (string.IsNullOrEmpty(textBoxSubscriptionRemarks.Text) ||
                string.IsNullOrEmpty(textBoxSubscriptionLink.Text))
                return;

            panelLoading.IsVisible = true;

            Status status = convertLinkToSubscription.Invoke(
                textBoxSubscriptionRemarks.Text,
                textBoxSubscriptionLink.Text);

            if (status.Code == Code.ERROR)
            {
                panelLoading.IsVisible = false;
                return;
            }

            string[] sub = (string[])status.Content;
            groupPath = "";
            onCreateSubscription.Invoke(sub[0], textBoxSubscriptionLink.Text, sub[1]);
            onUpdateConfig.Invoke(GetLastConfigPath(GroupType.SUBSCRIPTION));
            panelLoading.IsVisible = false;
            LoadGroupsList();
            LoadConfigsList(GroupType.SUBSCRIPTION);
            textBoxSubscriptionRemarks.Text = "";
            textBoxSubscriptionLink.Text = "";
            ShowServersPanel();
        }

        private void HandleEditSubscription()
        {
            var selected = comboBoxSubscription.SelectedItem;
            if (selected == null) return;

            var subscription = ((KeyValuePair<Subscription, string>)selected).Key;
            string oldRemarks = ((KeyValuePair<Subscription, string>)selected).Value;

            HandleImportingSubscription();

            if (oldRemarks != ((KeyValuePair<Subscription, string>)comboBoxSubscription.SelectedItem).Value)
            {
                onDeleteSubscription.Invoke(subscription);
                LoadGroupsList();
                LoadConfigsList(GroupType.SUBSCRIPTION);
            }
        }

        private void OnCancelButtonClick(object sender, RoutedEventArgs e)
        {
            ShowServersPanel();
        }

        private void OnSubscriptionComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (comboBoxSubscription.SelectedItem == null) return;
            var selected = (KeyValuePair<Subscription, string>)comboBoxSubscription.SelectedItem;
            groupPath = selected.Key.Directory.FullName;
            LoadConfigsList(GroupType.SUBSCRIPTION);
        }

        private void OnUpdateSubscriptionButtonClick(object sender, RoutedEventArgs e)
        {
            AnalyticsService.SendEvent(new SubUpdateButtonClickedEvent());
            if (comboBoxSubscription.SelectedItem == null) return;

            var selected = (KeyValuePair<Subscription, string>)comboBoxSubscription.SelectedItem;
            textBoxSubscriptionRemarks.Text = selected.Value;
            textBoxSubscriptionLink.Text = selected.Key.Url;

            HandleEditSubscription();
        }

        private void OnEditSubscriptionButtonClick(object sender, RoutedEventArgs e)
        {
            AnalyticsService.SendEvent(new SubEditButtonClickedEvent());
            if (comboBoxSubscription.SelectedItem == null) return;

            subscriptionOperation = SubscriptionOperation.EDIT;
            var selected = (KeyValuePair<Subscription, string>)comboBoxSubscription.SelectedItem;
            textBoxSubscriptionRemarks.Text = selected.Value;
            textBoxSubscriptionLink.Text = selected.Key.Url;

            panelServers.IsVisible = false;
            panelAddSubscriptions.IsVisible = true;
        }

        private void OnDeleteSubscriptionButtonClick(object sender, RoutedEventArgs e)
        {
            AnalyticsService.SendEvent(new SubDeleteButtonClickedEvent());
            if (comboBoxSubscription.SelectedItem == null) return;

            var selected = (KeyValuePair<Subscription, string>)comboBoxSubscription.SelectedItem;
            onDeleteSubscription.Invoke(selected.Key);
            LoadGroupsList();
            LoadConfigsList(GroupType.SUBSCRIPTION);
            onUpdateConfig.Invoke(GetLastConfigPath(GroupType.SUBSCRIPTION));
        }

        private void ShowServersPanel()
        {
            panelAddConfigs.IsVisible = false;
            panelAddSubscriptions.IsVisible = false;
            panelServers.IsVisible = true;
        }

        private void LoadConfigsList(GroupType group)
        {
            if (group == GroupType.GENERAL)
                LoadGeneralConfigsList();
            else
                LoadSubscriptionConfigsList();
        }

        private void LoadGeneralConfigsList()
        {
            List<Config> configs = getAllGeneralConfigs.Invoke();
            listConfigs.Children.Clear();

            textNoConfig.IsVisible = configs == null || configs.Count == 0;

            if (configs == null) return;

            foreach (Config config in configs)
            {
                var item = CreateConfigItem(config, GroupType.GENERAL);
                listConfigs.Children.Add(item);
            }
        }

        private void LoadSubscriptionConfigsList()
        {
            List<Config> configs = getAllSubscriptionConfigs?.Invoke(groupPath);
            List<Subscription> groups = getAllGroups?.Invoke() ?? new List<Subscription>();
            listSubscriptions.Children.Clear();

            panelSubscriptionControl.IsVisible = groups.Count > 0;
            textNoSubscription.IsVisible = (configs == null || configs.Count == 0) && groups.Count == 0;

            if (configs == null) return;

            foreach (Config config in configs)
            {
                var item = CreateConfigItem(config, GroupType.SUBSCRIPTION);
                listSubscriptions.Children.Add(item);
            }
        }

        private Border CreateConfigItem(Config config, GroupType group)
        {
            string currentPath = getCurrentConfigPath?.Invoke();
            bool isSelected = config.Path == currentPath;

            var border = new Border
            {
                Background = isSelected
                    ? Avalonia.Media.Brush.Parse("#3d3d3d")
                    : Avalonia.Media.Brushes.Transparent,
                Padding = new Avalonia.Thickness(15, 8),
                Cursor = new Avalonia.Input.Cursor(StandardCursorType.Hand)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var nameBlock = new TextBlock
            {
                Text = config.Name,
                Foreground = Avalonia.Media.Brushes.White,
                FontSize = 14,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(nameBlock, 0);

            var statusBlock = new TextBlock
            {
                Text = "",
                FontSize = 11,
                Foreground = Avalonia.Media.Brushes.Gray,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(8, 0),
                Tag = config.Path
            };
            Grid.SetColumn(statusBlock, 1);

            var deleteBtn = new Button
            {
                Content = "✕",
                FontSize = 12,
                Foreground = Avalonia.Media.Brushes.Gray,
                Background = Avalonia.Media.Brushes.Transparent,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Padding = new Avalonia.Thickness(4, 2)
            };
            Grid.SetColumn(deleteBtn, 2);

            var contextMenu = new ContextMenu();

            var selectItem = new MenuItem { Header = "Select" };
            selectItem.Click += (s, e) =>
            {
                onUpdateConfig.Invoke(config.Path);
                RefreshSelection();
                AnalyticsService.SendEvent(new SelectButtonClickedEvent());
            };
            contextMenu.Items.Add(selectItem);

            var checkItem = new MenuItem { Header = "Check" };
            checkItem.Click += (s, e) =>
            {
                statusBlock.Text = "...";
                statusBlock.Foreground = Avalonia.Media.Brushes.Gray;
                Task.Run(() =>
                {
                    try
                    {
                        int ping = testConnection.Invoke(config.Path);
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (ping >= 0)
                            {
                                statusBlock.Text = $"{ping}ms";
                                statusBlock.Foreground = ping < 300
                                    ? Avalonia.Media.Brush.Parse("#43b581")
                                    : Avalonia.Media.Brush.Parse("#faa61a");
                            }
                            else
                            {
                                statusBlock.Text = "timeout";
                                statusBlock.Foreground = Avalonia.Media.Brush.Parse("#f04747");
                            }
                        });
                    }
                    catch
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            statusBlock.Text = "error";
                            statusBlock.Foreground = Avalonia.Media.Brush.Parse("#f04747");
                        });
                    }
                });
                AnalyticsService.SendEvent(new CheckButtonClickedEvent());
            };
            contextMenu.Items.Add(checkItem);

            contextMenu.Items.Add(new Separator());

            var shareItem = new MenuItem { Header = "Share (Copy Path)" };
            shareItem.Click += (s, e) =>
            {
                try
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    clipboard?.SetTextAsync(config.Path);
                }
                catch { }
                AnalyticsService.SendEvent(new ShareButtonClickedEvent());
            };
            contextMenu.Items.Add(shareItem);

            var logItem = new MenuItem { Header = "Log" };
            logItem.Click += (s, e) =>
            {
                try
                {
                    string logPath = getLogPath?.Invoke();
                    if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
                        Process.Start("open", logPath);
                }
                catch { }
                AnalyticsService.SendEvent(new LogButtonClickedEvent());
            };
            contextMenu.Items.Add(logItem);

            contextMenu.Items.Add(new Separator());

            var deleteItem = new MenuItem { Header = "Delete" };
            deleteItem.Click += (s, e) =>
            {
                DeleteConfig(config, group);
                AnalyticsService.SendEvent(new DeleteButtonClickedEvent());
            };
            contextMenu.Items.Add(deleteItem);

            border.ContextMenu = contextMenu;

            deleteBtn.Click += (s, e) => DeleteConfig(config, group);

            border.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
                {
                    onUpdateConfig.Invoke(config.Path);
                    RefreshSelection();
                    AnalyticsService.SendEvent(new SelectButtonClickedEvent());
                }
            };

            grid.Children.Add(nameBlock);
            grid.Children.Add(statusBlock);
            grid.Children.Add(deleteBtn);
            border.Child = grid;

            return border;
        }

        private void DeleteConfig(Config config, GroupType group)
        {
            onDeleteConfig.Invoke(group, config.Path);
            groupPath = config.Path;
            if (group == GroupType.SUBSCRIPTION)
            {
                List<Config> remaining = getAllSubscriptionConfigs.Invoke(groupPath);
                if (remaining == null || remaining.Count == 0)
                    LoadGroupsList();
            }
            LoadConfigsList(group);
            if (getCurrentConfigPath.Invoke() == config.Path)
                onUpdateConfig.Invoke(GetLastConfigPath(group));
        }

        private void RefreshSelection()
        {
            string currentPath = getCurrentConfigPath?.Invoke();

            RefreshListSelection(listConfigs, currentPath);
            RefreshListSelection(listSubscriptions, currentPath);

            void RefreshListSelection(StackPanel list, string path)
            {
                foreach (var child in list.Children)
                {
                    if (child is Border b && b.Child is Grid g && g.Children.Count > 0)
                    {
                        var tb = g.Children[0] as TextBlock;
                        b.Background = tb?.Text != null && IsMatchingConfig(tb.Text, path)
                            ? Avalonia.Media.Brush.Parse("#3d3d3d")
                            : Avalonia.Media.Brushes.Transparent;
                    }
                }
            }

            bool IsMatchingConfig(string name, string path)
            {
                var allConfigs = getAllGeneralConfigs?.Invoke() ?? new List<Config>();
                var subConfigs = getAllSubscriptionConfigs?.Invoke(groupPath) ?? new List<Config>();
                var all = allConfigs.Concat(subConfigs);
                return all.Any(c => c.Name == name && c.Path == path);
            }
        }

        private void LoadGroupsList()
        {
            var groups = getAllGroups?.Invoke() ?? new List<Subscription>();
            var dict = new Dictionary<Subscription, string>();
            foreach (var group in groups)
                dict.Add(group, group.Directory.Name);

            comboBoxSubscription.ItemsSource = dict.ToList();
            comboBoxSubscription.DisplayMemberBinding = new Avalonia.Data.Binding("Value");
            comboBoxSubscription.SelectedValueBinding = new Avalonia.Data.Binding("Key");

            if (dict.Count > 0)
            {
                var current = dict.FirstOrDefault(g =>
                    g.Key.Directory.FullName == FileUtility.GetDirectory(groupPath));
                if (current.Key == null)
                    comboBoxSubscription.SelectedIndex = dict.Count - 1;
                else
                    comboBoxSubscription.SelectedItem = current;
            }
        }

        private string GetLastConfigPath(GroupType group)
        {
            if (group == GroupType.GENERAL)
            {
                var configs = getAllGeneralConfigs?.Invoke();
                if (configs != null && configs.Count > 0) return configs.Last().Path;
                var subConfigs = getAllSubscriptionConfigs?.Invoke(groupPath);
                if (subConfigs != null && subConfigs.Count > 0) return subConfigs.Last().Path;
            }
            else
            {
                var configs = getAllSubscriptionConfigs?.Invoke(groupPath);
                if (configs != null && configs.Count > 0) return configs.Last().Path;
                var genConfigs = getAllGeneralConfigs?.Invoke();
                if (genConfigs != null && genConfigs.Count > 0) return genConfigs.Last().Path;
            }
            return null;
        }

        private void SetActiveFileImportingGroup(bool isActive)
        {
            textBoxConfigLink.Text = "";
            buttonConfigFile.IsEnabled = isActive;
        }

        private void SetActiveLinkImportingGroup(bool isActive)
        {
            configPath = null;
            textBlockFileName.Text = "No file chosen";
            textBoxConfigLink.IsEnabled = isActive;
        }

        private void HideAllPanels()
        {
            panelConfig.IsVisible = false;
            panelSubscription.IsVisible = false;
        }

        private void EnableAllTabs()
        {
            buttonConfigTab.IsEnabled = true;
            buttonSubscriptionTab.IsEnabled = true;
        }
    }
}
