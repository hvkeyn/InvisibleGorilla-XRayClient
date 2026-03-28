# Product Context

## Why This Exists
Users need a simple Xray client that hides raw Xray-core complexity behind a familiar UI for importing configs, managing subscriptions, and routing traffic.

## User Expectations
- Desktop and Android should feel like one product family.
- Android should behave like a real VPN client, not just expose a local proxy listener.
- Starting and stopping a connection must be obvious, fast, and reliable.
- Configs, subscriptions, sharing, deletion, and availability checks should work from the UI without manual file management.

## Android UX Priorities
- Match the Windows/macOS visual structure as closely as practical.
- Show connection state clearly on the home screen.
- Keep important runtime details visible through in-app status text and Android notifications.
- Make link/file import easy on mobile, including clipboard-based paste flows.

## High-Value Validation
- Real browser traffic must switch to the selected config when VPN is active.
- Stopping the VPN must restore the normal network path and must not trigger ANR dialogs.
