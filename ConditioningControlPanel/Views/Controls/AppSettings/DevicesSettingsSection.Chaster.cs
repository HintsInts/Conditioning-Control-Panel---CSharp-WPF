using System;
using System.Windows;
using ConditioningControlPanel.Services.Integrations.Chaster;

namespace ConditioningControlPanel.Views.Controls.AppSettingsSections
{
    public partial class DevicesSettingsSection
    {
        private bool _chasterStateSubscribed;

        private async void ChasterCard_Loaded(object sender, RoutedEventArgs e)
        {
            var client = ChasterCcpClient.Instance;
            client.Initialize();
            if (!_chasterStateSubscribed)
            {
                client.StateChanged += ChasterClient_StateChanged;
                _chasterStateSubscribed = true;
            }

            var snapshot = client.GetSnapshot();
            if (string.IsNullOrWhiteSpace(TxtChasterServerUrl.Text) && !string.IsNullOrWhiteSpace(snapshot.BaseUrl))
                TxtChasterServerUrl.Text = snapshot.BaseUrl;
            RenderChasterSnapshot(snapshot);
            if (snapshot.IsPaired)
                await client.CheckConnectionAsync();
        }

        private void ChasterCard_Unloaded(object sender, RoutedEventArgs e)
        {
            if (!_chasterStateSubscribed) return;
            ChasterCcpClient.Instance.StateChanged -= ChasterClient_StateChanged;
            _chasterStateSubscribed = false;
        }

        private void ChasterClient_StateChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(ChasterClient_StateChanged));
                return;
            }
            RenderChasterSnapshot(ChasterCcpClient.Instance.GetSnapshot());
        }

        private async void BtnChasterPair_Click(object sender, RoutedEventArgs e)
        {
            BtnChasterPair.IsEnabled = false;
            TxtChasterStatus.Text = "Pairing…";
            try
            {
                await ChasterCcpClient.Instance.PairAsync(
                    TxtChasterServerUrl.Text,
                    TxtChasterPairCode.Text,
                    "Conditioning Control Panel");
                TxtChasterPairCode.Clear();
                await ChasterCcpClient.Instance.CheckConnectionAsync();
            }
            catch (Exception ex)
            {
                TxtChasterStatus.Text = "Pairing failed: " + ex.Message;
            }
            finally
            {
                BtnChasterPair.IsEnabled = true;
                RenderChasterSnapshot(ChasterCcpClient.Instance.GetSnapshot());
            }
        }

        private async void BtnChasterRefresh_Click(object sender, RoutedEventArgs e)
        {
            BtnChasterRefresh.IsEnabled = false;
            try
            {
                await ChasterCcpClient.Instance.CheckConnectionAsync();
                await ChasterCcpClient.Instance.FlushNowAsync();
            }
            finally
            {
                BtnChasterRefresh.IsEnabled = true;
                RenderChasterSnapshot(ChasterCcpClient.Instance.GetSnapshot());
            }
        }

        private async void BtnChasterDisconnect_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Disconnect this CCP installation from the Chaster extension?\n\nPending local events are not deleted.",
                "Disconnect Chaster",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            BtnChasterDisconnect.IsEnabled = false;
            try
            {
                await ChasterCcpClient.Instance.DisconnectAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "The server could not revoke this device, so CCP kept the credential locally.\n\n" + ex.Message,
                    "Could not disconnect Chaster",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                BtnChasterDisconnect.IsEnabled = true;
                RenderChasterSnapshot(ChasterCcpClient.Instance.GetSnapshot());
            }
        }

        private void RenderChasterSnapshot(ChasterClientSnapshot snapshot)
        {
            TxtChasterStatus.Text = snapshot.Status;
            TxtChasterOutbox.Text = snapshot.DeadLetterEvents > 0
                ? $"Pending events: {snapshot.PendingEvents}  •  Needs review: {snapshot.DeadLetterEvents}"
                : $"Pending events: {snapshot.PendingEvents}";
            BtnChasterDisconnect.IsEnabled = snapshot.IsPaired;
            BtnChasterRefresh.IsEnabled = snapshot.IsPaired;
            if (!string.IsNullOrWhiteSpace(snapshot.BaseUrl) && string.IsNullOrWhiteSpace(TxtChasterServerUrl.Text))
                TxtChasterServerUrl.Text = snapshot.BaseUrl;
        }
    }
}
