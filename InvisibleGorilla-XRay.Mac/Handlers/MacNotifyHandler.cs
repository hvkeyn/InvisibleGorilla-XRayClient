using System;
using InvisibleGorillaXRay.Handlers;
using InvisibleGorillaXRay.Models;

namespace InvisibleGorillaXRay.Mac.Handlers
{
    public class MacNotifyHandler : Handler
    {
        private Func<Mode> getMode;
        private Action onOpenClick;
        private Action onUpdateClick;
        private Action onAboutClick;
        private Action onCloseClick;
        private Action onProxyModeClick;
        private Action onTunnelModeClick;

        public void Setup(
            Func<Mode> getMode, Action onOpenClick, Action onUpdateClick,
            Action onAboutClick, Action onCloseClick,
            Action onProxyModeClick, Action onTunnelModeClick)
        {
            this.getMode = getMode;
            this.onOpenClick = onOpenClick;
            this.onUpdateClick = onUpdateClick;
            this.onAboutClick = onAboutClick;
            this.onCloseClick = onCloseClick;
            this.onProxyModeClick = onProxyModeClick;
            this.onTunnelModeClick = onTunnelModeClick;
        }

        public void InitializeNotifyIcon() { }
        public void CheckModeItem() { }
    }
}
