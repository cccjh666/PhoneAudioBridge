# PhoneAudioBridge

Use your Windows PC as a Bluetooth audio receiver (A2DP sink) for your phone.

Includes connection-failure diagnostics, audio-endpoint volume control, automatic reconnection, and detection of the "connected but silent" state.

> This is an independent third-party tool. It is not affiliated with, endorsed by, or sponsored by Microsoft, the Bluetooth SIG, or the developer of any similarly named app in an app store. See [NOTICE.md](NOTICE.md).

[中文说明](README.md) · [Changelog](CHANGELOG.md)

![Main window](docs/screenshot-connected.png)

---

## Why this exists

Since Windows 10 version 2004 (build 19041), Windows exposes a public API, `Windows.Media.Audio.AudioPlaybackConnection`, that lets a PC receive audio from a paired phone. Microsoft ships no desktop UI for it.

A commercial app in the Microsoft Store implements the same feature and has publicly documented volume problems: volume pinned at maximum with no way to change it, and volume control failing mid-playback until the connection is re-established (see citations). That app is closed-source, so it cannot be patched.

This project reimplements the feature on **the same system API**, with three concrete goals: report failures precisely, get volume control right, and recover from disconnections on its own.

## Features

- **Precise failure reasons.** The status code returned by `Open()` distinguishes success, phone timeout, rejection by the system, and unknown failure. Each is surfaced as an actionable message instead of a generic "connection failed".
- **Bounded automatic reconnection.** Backoff of 1.5 → 3 → 6 → 12 → 24 → 30 seconds for 6 attempts, then a low-frequency probe every 30 seconds for up to ~20 minutes. The slow phase exists because during a phone call the link is taken over by hands-free mode and usually comes back afterwards.
- **Volume via the audio endpoint.** The slider drives `IAudioEndpointVolume` on the current default playback device, not the system volume flyout. Changes apply immediately, values do not jump, and mute is lifted automatically when you set a volume.
- **Volume memory.** The level is stored on disconnect/exit and restored after the next successful connection.
- **No stale device list.** Devices and the volume endpoint are re-enumerated before each connection, so swapping headphones or re-pairing does not require a restart.
- **"Connected but silent" detection.** Peak levels are sampled every 2 seconds. If audio was heard during the current session and then stays silent for 60 seconds, a hint is shown. This state still reports "connected", which is why it is hard to diagnose unaided.

Also: tray residency, icon colour reflecting state (grey/orange/green/red), double-click to restore, run-at-login, single instance, and full logging.

## Requirements

- Windows 10 version 2004 (build 19041) or later. Tested on Windows 11.
- The phone must already be paired with the PC. Unpaired devices are not listed and cannot be paired by this app.
- The portable release does **not** require the .NET runtime.

## Install and use

1. Download `PhoneAudioBridge.exe` from [Releases](../../releases).
2. Run it. The binary is unsigned, so SmartScreen may warn about an unknown publisher — choose "More info" → "Run anyway".
3. Pair your phone once (Settings → Bluetooth & devices → Add device).
4. Pick the phone in the window and press Connect.
5. Play audio on the phone; it comes out of the PC's current default playback device.
6. The ✕ button only **hides the window to the tray**. To quit, right-click the tray icon and choose Exit.

Logs live in `%LOCALAPPDATA%\PhoneAudioBridge\logs\`.

## Known limitations

Most of these are boundaries of the system API or the Bluetooth profiles, not bugs.

- **Call audio cannot be forwarded.** Voice/video calls need the microphone, which switches the link to hands-free (HFP) mode; the system then discards audio destined for the stereo (A2DP) stream, and the Windows Bluetooth driver does not provide the HFP client role needed for phone→PC audio. This affects every app in this category.
- **Volume has two layers.** Phone media volume scales audio before encoding; PC output volume scales playback. The slider controls only the latter.
- **Noticeable latency.** Inherent to the Bluetooth audio path; video will be slightly out of sync.
- **Codec limits.** The built-in A2DP sink uses SBC. Quality and maximum loudness are modest.
- **Unsigned binary.** No code signing, therefore no way to avoid SmartScreen and no auto-update.

## FAQ

**My phone is not listed.** Pair it in Windows Settings first. Only paired devices with audio sink capability are enumerated.

**It says connected but there is no sound.** Check that the phone is actually playing. If the "connected but no audio stream" hint appears, the usual causes are: output routed to the phone speaker, an active call, or volume at zero. The log records every endpoint volume change, which tells you whether volume commands arrived.

**Changing volume on the phone does not affect the PC.** The phone is not sending AVRCP absolute-volume commands. Most Android devices have a "Disable absolute volume" toggle in Developer options; turning it off (i.e. allowing absolute volume) makes the phone drive PC volume. Note that it is bidirectional — the phone can then also change your PC volume.

**The process stays alive after closing the window.** Intended; it lives in the tray. Use Exit from the tray menu.

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
git clone <this repository>
cd PhoneAudioBridge

dotnet build src/PhoneAudioBridge/PhoneAudioBridge.csproj -c Release

dotnet publish src/PhoneAudioBridge/PhoneAudioBridge.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -o publish
```

Output: `publish/PhoneAudioBridge.exe`, roughly 70 MB including the .NET runtime.

Stack: .NET 8 + WPF. Bluetooth via the WinRT `AudioPlaybackConnection` API (CsWinRT projection, callable from an unpackaged desktop app). Volume via NAudio's `MMDeviceEnumerator` / `IAudioEndpointVolume`.

## Privacy

- No network requests, no telemetry, no auto-update, no account.
- Configuration and logs stay local, in `%LOCALAPPDATA%\PhoneAudioBridge\`.
- Logs contain device names and volume values; check them before pasting into an issue.

## Related projects

Other open-source implementations on the same API. Listed for comparison only; no affiliation.

- [ysc3839/AudioPlaybackConnector](https://github.com/ysc3839/AudioPlaybackConnector) — C++
- [DylanUncle/Audio_Bridge](https://github.com/DylanUncle/Audio_Bridge) — C++
- [AkiroMusic/AkiLink](https://github.com/AkiroMusic/AkiLink) — C# WPF
- [blameTheCache/soundbridge](https://github.com/blameTheCache/soundbridge) — C#
- [tiwut/Bluetooth-Audio-Receiver](https://github.com/tiwut/Bluetooth-Audio-Receiver) — C#
- [byzp/bluetooth_audio_cli](https://github.com/byzp/bluetooth_audio_cli) — C++ CLI

## License

MIT — see [LICENSE](LICENSE). Dependency licenses are listed in [NOTICE.md](NOTICE.md).

## Citations

“AudioPlaybackConnection Class.” *Microsoft Learn*, Microsoft, learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnection.

“Bluetooth Classic Audio.” *Microsoft Learn*, Microsoft, learn.microsoft.com/en-us/windows-hardware/drivers/bluetooth/bluetooth-classic-audio.

“Re: Cannot Use PC as Bluetooth Headset for Smartphone. Win 10.” *Intel Community*, Intel Corporation, community.intel.com/t5/Wireless/Cannot-use-PC-as-Bluetooth-headset-for-smartphone-Win-10/m-p/525280.

“The ‘Bluetooth Audio Receiver’ App Is at a Fixed Volume.” *Microsoft Q&A*, Microsoft, 6 July 2025, learn.microsoft.com/en-us/answers/a/8211308.
