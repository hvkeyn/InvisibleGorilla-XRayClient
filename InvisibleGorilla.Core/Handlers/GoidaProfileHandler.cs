using System;
using InvisibleGorillaXRay.Models;
using InvisibleGorillaXRay.Services.Goida;

namespace InvisibleGorillaXRay.Handlers
{
    public class GoidaProfileHandler : Handler
    {
        private GoidaProfileManager manager = new();
        private Func<UserSettings> getUserSettings;
        private Action<UserSettings> updateUserSettings;

        public GoidaProfileManager Manager => manager;

        public void Setup(
            Func<string, Status> convertConfigLinkToV2Ray,
            Func<string, int> testConnection,
            Func<UserSettings> getUserSettings,
            Action<UserSettings> updateUserSettings,
            Action<GoidaNode>? onActiveNodeChanged = null,
            Func<bool>? pauseNativeForTest = null,
            Action? resumeNativeAfterTest = null)
        {
            this.getUserSettings = getUserSettings;
            this.updateUserSettings = updateUserSettings;

            manager.Setup(
                convertConfigLinkToV2Ray: convertConfigLinkToV2Ray,
                testConnection: testConnection,
                getSettings: () => getUserSettings().GetGoidaSettings().Clone(),
                saveSettings: SaveGoidaSettings,
                onActiveNodeChanged: onActiveNodeChanged,
                pauseNativeForTest: pauseNativeForTest,
                resumeNativeAfterTest: resumeNativeAfterTest);
        }

        public void StartBackground() => manager.Start();

        public void StopBackground() => manager.Stop();

        private void SaveGoidaSettings(GoidaProfileSettings settings)
        {
            UserSettings current = getUserSettings();
            current.Goida = settings.Clone();
            updateUserSettings(current);
        }
    }
}
