using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using InvisibleGorillaXRay.Core;
using InvisibleGorillaXRay.Handlers.OpenFlux;
using InvisibleGorillaXRay.Models;
using InvisibleGorillaXRay.Services.OpenFlux;

namespace InvisibleGorillaXRay.Android.Views
{
    public partial class MainView
    {
        private bool isApplyingOpenFluxUi;
        private bool openFluxUrlHooked;
        private string? pendingOpenFluxUrl;
        private CancellationTokenSource? openFluxCheckCts;

        private Border OpenFluxEditor => GetRequiredControl<Border>("OpenFluxEditorPanel");
        private TextBox OpenFluxUrlInput => GetRequiredControl<TextBox>("OpenFluxUrlTextBox");
        private TextBox OpenFluxKeyInput => GetRequiredControl<TextBox>("OpenFluxKeyTextBox");
        private ComboBox OpenFluxTransportSelector => GetRequiredControl<ComboBox>("OpenFluxTransportComboBox");
        private Button OpenFluxApplyActionButton => GetRequiredControl<Button>("OpenFluxApplyButton");
        private TextBlock OpenFluxStatusText => GetRequiredControl<TextBlock>("OpenFluxStatusTextBlock");
        private TextBlock OpenFluxHintText => GetRequiredControl<TextBlock>("OpenFluxHintTextBlock");
        private TextBlock OpenFluxUrlLabelText => GetRequiredControl<TextBlock>("OpenFluxUrlLabelTextBlock");
        private TextBlock OpenFluxKeyLabelText => GetRequiredControl<TextBlock>("OpenFluxKeyLabelTextBlock");

        private bool IsOpenFluxProfileActive()
        {
            return OpenFluxProfilePaths.IsMarker(settingsHandler.UserSettings.GetCurrentConfigPath());
        }

        private void InitializeOpenFluxControls()
        {
            if (!openFluxUrlHooked)
            {
                OpenFluxUrlInput.TextChanged += (_, _) => OnOpenFluxUrlTextChanged();
                openFluxUrlHooked = true;
            }

            if (OpenFluxTransportSelector.Items.Count == 0)
            {
                OpenFluxTransportSelector.Items.Add(Localize("Lang.OpenFlux.Transport.Auto"));
                OpenFluxTransportSelector.Items.Add(Localize("Lang.OpenFlux.Transport.Vyandex"));
                OpenFluxTransportSelector.Items.Add(Localize("Lang.OpenFlux.Transport.Yandex"));
            }

            core.GetOpenFluxManager().StatusChanged += OnOpenFluxStatusChanged;
            ApplyOpenFluxLocalizedText();
            ApplyOpenFluxPanel();
        }

        private void ApplyOpenFluxLocalizedText()
        {
            OpenFluxUrlLabelText.Text = Localize("Lang.OpenFlux.UrlLabel");
            OpenFluxKeyLabelText.Text = Localize("Lang.OpenFlux.KeyLabel");
            OpenFluxHintText.Text = Localize("Lang.OpenFlux.Hint");
            OpenFluxApplyActionButton.Content = Localize("Lang.OpenFlux.Apply");
            OpenFluxUrlInput.Watermark = "https://disk.yandex.ru/edit/d/...";
            OpenFluxKeyInput.Watermark = Localize("Lang.OpenFlux.KeyHint");
        }

        private void ApplyOpenFluxPanel()
        {
            bool active = IsOpenFluxProfileActive();
            OpenFluxEditor.IsVisible = active;
            if (!active)
                return;

            OpenFluxProfile profile = settingsHandler.UserSettings.GetOpenFluxProfile();
            isApplyingOpenFluxUi = true;
            try
            {
                string canonicalUrl = OpenFluxUrl.Trim(profile.DocUrl);
                if (OpenFluxUrlInput.Text != canonicalUrl)
                    OpenFluxUrlInput.Text = canonicalUrl;
                RefreshOpenFluxDerivedKey();
                OpenFluxTransportSelector.SelectedIndex = profile.Transport switch
                {
                    OpenFluxTransportMode.Vyandex => 1,
                    OpenFluxTransportMode.Yandex => 2,
                    _ => 0
                };
            }
            finally
            {
                isApplyingOpenFluxUi = false;
            }

            ApplyOpenFluxStatusText(
                core.GetOpenFluxManager().Status,
                core.GetOpenFluxManager().StatusDetail);
        }

        private void OnOpenFluxUrlTextChanged()
        {
            if (isApplyingOpenFluxUi)
                return;

            RefreshOpenFluxDerivedKey();
            ScheduleOpenFluxUrlCheck();
        }

        private void RefreshOpenFluxDerivedKey()
        {
            string key = OpenFluxUrl.DeriveKey(OpenFluxUrlInput.Text);
            if (OpenFluxKeyInput.Text != key)
                OpenFluxKeyInput.Text = key;
        }

        private OpenFluxProfile ReadOpenFluxFromUi()
        {
            OpenFluxProfile profile = settingsHandler.UserSettings.GetOpenFluxProfile().Clone();
            profile.DocUrl = OpenFluxUrl.Trim(OpenFluxUrlInput.Text);
            profile.EncryptionKey = OpenFluxUrl.DeriveKey(profile.DocUrl);
            profile.ConfigPath = OpenFluxProfilePaths.MarkerPath;
            profile.Transport = OpenFluxTransportSelector.SelectedIndex switch
            {
                1 => OpenFluxTransportMode.Vyandex,
                2 => OpenFluxTransportMode.Yandex,
                _ => OpenFluxTransportMode.Auto
            };
            return profile;
        }

        private async void OnOpenFluxApplyClick(object? sender, RoutedEventArgs e)
        {
            pendingOpenFluxUrl = OpenFluxUrl.Trim(OpenFluxUrlInput.Text);
            OpenFluxApplyActionButton.IsEnabled = false;
            try
            {
                string url = pendingOpenFluxUrl ?? string.Empty;
                pendingOpenFluxUrl = null;
                OpenFluxProfile profile = ReadOpenFluxFromUi();
                profile.DocUrl = url;
                profile.EncryptionKey = OpenFluxUrl.DeriveKey(url);

                if (OpenFluxUrlInput.Text != url)
                    OpenFluxUrlInput.Text = url;

                OpenFluxUrlCheckResult check = await OpenFluxUrlCheck.InspectAsync(url, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(check.Error))
                {
                    OpenFluxStatusText.Text = MapOpenFluxCheckError(check.Error);
                    return;
                }

                if (check.LatencyMs > 0)
                    OpenFluxStatusText.Text = string.Format(Localize("Lang.OpenFlux.Status.UrlOk"), check.LatencyMs);

                settingsHandler.UpdateOpenFlux(profile);
                Status result = await Task.Run(() => core.ApplyOpenFluxDocUrl(url));
                if (result.Code == Code.SUCCESS)
                {
                    string tag = result.Content?.ToString() ?? string.Empty;
                    OpenFluxStatusText.Text = tag == "register-fail"
                        ? Localize("Lang.OpenFlux.Status.RegisterFail")
                        : Localize("Lang.OpenFlux.Status.Registered");
                }
                else
                {
                    OpenFluxStatusText.Text = Localize(MapOpenFluxApplyError(result.Content?.ToString()));
                }

                RefreshConfigs();
                ApplyOpenFluxPanel();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.OpenFlux.Apply", ex);
                OpenFluxStatusText.Text = ex.Message;
            }
            finally
            {
                OpenFluxApplyActionButton.IsEnabled = true;
            }
        }

        private void ScheduleOpenFluxUrlCheck()
        {
            openFluxCheckCts?.Cancel();
            openFluxCheckCts = new CancellationTokenSource();
            CancellationToken token = openFluxCheckCts.Token;
            _ = RunOpenFluxUrlCheckAsync(token);
        }

        private async Task RunOpenFluxUrlCheckAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(400, token);
                string url = OpenFluxUrl.Trim(OpenFluxUrlInput.Text);
                if (!OpenFluxUrl.TryValidate(url, out _))
                    return;

                OpenFluxUrlCheckResult check = await OpenFluxUrlCheck.InspectAsync(url, token);
                if (token.IsCancellationRequested)
                    return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!string.IsNullOrWhiteSpace(check.Error))
                        OpenFluxStatusText.Text = MapOpenFluxCheckError(check.Error);
                    else if (check.LatencyMs > 0)
                        OpenFluxStatusText.Text = string.Format(Localize("Lang.OpenFlux.Status.UrlOk"), check.LatencyMs);
                });
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("MainView.OpenFlux.Check", ex);
            }
        }

        private void OnOpenFluxStatusChanged(OpenFluxClientStatus status, string detail)
        {
            Dispatcher.UIThread.Post(() => ApplyOpenFluxStatusText(status, detail));
        }

        private void ApplyOpenFluxStatusText(OpenFluxClientStatus status, string detail)
        {
            if (!IsOpenFluxProfileActive())
                return;

            OpenFluxStatusText.Text = status switch
            {
                OpenFluxClientStatus.Connecting => Localize("Lang.OpenFlux.Status.Connecting"),
                OpenFluxClientStatus.WaitingPeer when detail == "peer" => Localize("Lang.OpenFlux.Status.PeerMissing"),
                OpenFluxClientStatus.WaitingPeer => Localize("Lang.OpenFlux.Status.WaitingPeer"),
                OpenFluxClientStatus.Connected => Localize("Lang.OpenFlux.Status.Live"),
                OpenFluxClientStatus.Error => Localize(MapOpenFluxApplyError(detail)),
                _ => OpenFluxStatusText.Text
            };
        }

        private async Task CheckOpenFluxProfileAsync()
        {
            string url = OpenFluxUrl.Trim(
                string.IsNullOrWhiteSpace(OpenFluxUrlInput.Text)
                    ? settingsHandler.UserSettings.GetOpenFluxProfile().DocUrl
                    : OpenFluxUrlInput.Text);
            if (!OpenFluxUrl.TryValidate(url, out _))
            {
                SetStatus(Localize("Lang.OpenFlux.Error.BadUrl"));
                return;
            }

            OpenFluxUrlCheckResult check = await OpenFluxUrlCheck.InspectAsync(url, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(check.Error))
                SetStatus(MapOpenFluxCheckError(check.Error));
            else
                SetStatus(string.Format(Localize("Lang.OpenFlux.Status.UrlOk"), check.LatencyMs));
        }

        private string MapOpenFluxCheckError(string error)
        {
            return error switch
            {
                "login" => Localize("Lang.OpenFlux.Error.Login"),
                "timeout" => Localize("Lang.OpenFlux.Error.Timeout"),
                "http" => Localize("Lang.OpenFlux.Error.Http"),
                _ => Localize("Lang.OpenFlux.Error.BadUrl")
            };
        }

        private static string MapOpenFluxApplyError(string? detail)
        {
            return detail switch
            {
                "empty" or "scheme" or "host" => "Lang.OpenFlux.Error.BadUrl",
                "key-short" => "Lang.OpenFlux.Error.KeyShort",
                "document" => "Lang.OpenFlux.Error.Document",
                "transport" => "Lang.OpenFlux.Error.Transport",
                "listen" => "Lang.OpenFlux.Error.Listen",
                _ => "Lang.OpenFlux.Error.Generic"
            };
        }
    }
}
