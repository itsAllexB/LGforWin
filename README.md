# LGforWin

A small native **WinUI 3** Windows app to control the **brightness (OLED Light)** of
LG webOS TVs used as PC monitors — C2, C3, G-series and friends — over your LAN.

OLED TVs have no DDC/CI hardware brightness, so the Windows brightness slider does
nothing. The only real "brightness" control is the TV's **OLED Light (backlight)**
picture setting. LGforWin drives that setting over the network so you can change it
from your desk — with a slider, the system tray, or global hotkeys.

<p align="center">
  <img src="docs/screenshots/home.png" alt="LGforWin home screen" width="820">
</p>

## Features

- Add TVs by IP; **pair once** (accept the on-screen prompt), then it reconnects
  silently forever using the stored client-key. Secure `wss://:3001` with automatic
  fallback to `ws://:3000`.
- Per-TV **OLED Light slider (0–100)** that pushes changes live (debounced).
- **Global hotkeys** (customizable combo) that adjust the TV on the screen your
  **cursor** is on — pair each TV to its Windows monitor so it knows which is which.
- **On-screen display** — a Windows 11-style brightness overlay on the hotkey, with
  configurable position, screen, duration and optional TV name.
- **Schedules** — up to 5 daily brightness rules, with catch-up on startup.
- **System tray** icon — closing hides to tray; optional **launch at sign-in**
  (normal window / minimized / tray).
- **Import / export** all settings; light / dark / system theme.
- Remembers multiple TVs, their last brightness, and the window size.

## On-screen display

Nudge brightness with the hotkeys and a Windows 11-style overlay slides in — on the
screen of the TV you're adjusting:

<p align="center">
  <img src="docs/screenshots/overlay.png" alt="Brightness on-screen display overlay">
</p>

## Screens

|  |  |
|:--|:--|
| **Schedules** — up to 5 daily rules | **Hotkeys** — cursor-targeted, customizable combo |
| ![Schedules](docs/screenshots/schedule.png) | ![Hotkeys](docs/screenshots/hotkeys.png) |
| **On-screen display settings** | **Settings** |
| ![On-screen display settings](docs/screenshots/osd.png) | ![Settings](docs/screenshots/settings.png) |

## How it works (protocol)

webOS speaks **SSAP over a WebSocket** at `ws://<tv-ip>:3000`:

1. **Pair** — send a registration handshake. The TV shows an accept prompt and returns
   a `client-key`, which we persist and reuse so it never prompts again.
2. **Set brightness** — `ssap://com.webos.settingsservice/setSystemSettings` with
   `{ "category": "picture", "settings": { "backlight": "<0-100>" } }`.
   (Falls back to `ssap://settings/setSystemSettings` on firmware that rejects the
   internal URI.)
3. **Read current** — `ssap://settings/getSystemSettings` to initialise the slider.

Works on **stock TVs — no rooting required.** Prior art: bscpylgtv, ColorControl, lgtv2.

## Project layout

| Path | Purpose |
|------|---------|
| `Services/WebOsClient.cs` | Low-level SSAP WebSocket: connect, pair, request/response |
| `Services/TvController.cs` | Per-TV lifecycle, auto-reconnect, debounced SetBacklight |
| `Services/SsapPayloads.cs` | Registration manifest + SSAP URIs |
| `Services/DeviceStore.cs` | JSON persistence in `%LOCALAPPDATA%\LGforWin` |
| `Services/HotkeyService.cs` | Global hotkeys via `RegisterHotKey` |
| `ViewModels/` | `MainViewModel`, `TvViewModel` (CommunityToolkit.Mvvm) |
| `Views/MainWindow.xaml` | TV list, sliders, add/remove, tray icon |
| `Program.cs` / `App.xaml.cs` | Custom entry point, single-instance, startup wiring |

## Building

**Prerequisites**

- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)
- Visual Studio 2022/2026 with the **.NET desktop development** workload (provides the
  WinUI/MSIX MSBuild tasks; the standalone .NET SDK alone is not enough).

**Build & run**

Open `LGforWin.sln` in Visual Studio and press **F5**, or from a terminal:

```powershell
pwsh -File build.ps1 -Configuration Debug -Run
```

> Note: build with **Visual Studio's MSBuild** (what `build.ps1` does), not
> `dotnet build` — WinUI PRI generation needs the AppxPackage task that only ships
> with VS, so `dotnet build` fails with `MSB4062`.

## Usage

1. Make sure the TV is on the same network and **"LG Connect Apps" / mobile control**
   is enabled (Settings → Network/Connection).
2. Launch LGforWin, enter a name + the TV's IP, click **Add TV**.
3. Accept the pairing prompt on the TV (one time).
4. Drag the slider — brightness changes within ~150 ms. Use `Ctrl+Alt+Up/Down` anytime.

## Roadmap

Planned features and ideas are tracked in [`Todo.md`](Todo.md).

## License

Licensed under the [GNU General Public License v3.0](LICENSE) © Alex Bolocan.
