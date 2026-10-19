<!-- markdownlint-disable MD033 MD041 -->
<p align="center">
  <img src="Logo/logo_about.png" alt="Orynivo" width="720">
</p>

# Orynivo

**A free, open-source Hi-Fi music library, player, and self-hosted server for
Windows, Linux, and macOS — for people who still own their music.**

If you like what Roon does, but want software that is free, open source, and
keeps your files and listening data yours, Orynivo is built for you.

[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![Release](https://img.shields.io/github/v/release/bschlaack/Orynivo)](https://github.com/bschlaack/Orynivo/releases/latest)
![Platforms](https://img.shields.io/badge/platforms-Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-lightgrey)

**Website:** [orynivo.app](https://orynivo.app/) · **Downloads:**
[latest release](https://github.com/bschlaack/Orynivo/releases/latest) ·
**Docs:** [GitHub Wiki](https://github.com/bschlaack/Orynivo/wiki) ·
**Full feature reference:** [docs/FULL-FEATURE-REFERENCE.md](docs/FULL-FEATURE-REFERENCE.md)

## See Orynivo in action

<!-- markdownlint-disable MD034 -->

https://github.com/user-attachments/assets/92e1cde3-b787-4752-8ad9-944d47d0b32f

<!-- markdownlint-enable MD034 -->

If the embedded video does not play in your browser,
[watch it on YouTube](https://www.youtube.com/watch?v=llYTFwNLZAY).

## Screenshots

<table>
  <tr>
    <td><img src="html/assets/screenshots/dashboard.webp" alt="Dashboard" width="400"></td>
    <td><img src="html/assets/screenshots/albums.webp" alt="Albums" width="400"></td>
  </tr>
  <tr>
    <td><img src="html/assets/screenshots/artists.webp" alt="Artists" width="400"></td>
    <td><img src="html/assets/screenshots/tracks.webp" alt="Tracks" width="400"></td>
  </tr>
  <tr>
    <td><img src="html/assets/screenshots/cloud.webp" alt="Genre Cloud" width="400"></td>
    <td><img src="html/assets/screenshots/ai-chat.webp" alt="AI Chat" width="400"></td>
  </tr>
</table>

## Why Orynivo?

- **Bit-perfect audio.** Exclusive WASAPI, cwASIO, and Steinberg ASIO output on
  Windows; direct ALSA hardware profiles on Linux with native DSD (DSF/DFF) and
  no ASIO bridge required. DoP fallback, gapless playback, ReplayGain, +6 dB PCM
  boost, and a parametric EQ with Equalizer APO / AutoEQ import.
- **Your library, everywhere.** Orynivo Server is a headless server for Linux,
  macOS, and Windows that exposes your library over the local network via REST
  and HTTP streaming. Desktop clients merge local and remote libraries into one
  unified Artists / Albums / Tracks experience.
- **Made for large collections.** Artists, albums, tracks, and folder views;
  CUE sheets and MKA chapters; smart playlists; full-text search across local
  and remote sources; bulk favorite/rating/genre editing that never rewrites
  your media files.
- **Discovery built in.** A hierarchical Genre Cloud across all connected
  sources, Infinite Mix that keeps a queue replenished from your listening
  habits, internet radio, podcasts, and lyrics.
- **AirPlay to your Hi-Fi.** RAOP receiver discovery on every platform plus a
  native AirPlay 2 bridge, verified with clean audible playback on a Sonos
  stereo pair.
- **AI that stays local.** Embedded AI chat through LM Studio, Ollama, or any
  OpenAI-compatible endpoint, and an MCP server with 37 individually
  toggleable tools for external assistants such as Claude Desktop.
- **Private by design.** Apache-2.0 licensed, no telemetry, no account. API
  keys and tokens live in an encrypted per-user credential store. The full UI
  is available in English, German, French, Spanish, Russian, Simplified
  Chinese, and Hindi.

## Download

Self-contained builds (the .NET runtime is bundled; no prerequisites) from the
[latest release](https://github.com/bschlaack/Orynivo/releases/latest):

| Component        | Packages                                                            |
| ---------------- | ------------------------------------------------------------------- |
| Windows player   | `Setup.exe`, portable ZIP (Windows 10/11 x64)                       |
| Linux player     | DEB, RPM, Arch `.pkg.tar.zst`, portable tar.gz (x64 and ARM64)      |
| macOS player     | PKG and portable app ZIP (Apple Silicon and Intel)                  |
| Orynivo Server   | DEB and RPM (Linux x64 / ARM64, incl. Raspberry Pi)                 |

The signed in-app updater installs updates through your native package manager
on Linux and verifies download digests on every platform.

## Orynivo Server

`Orynivo.Server` scans the same library directories as the desktop app and
serves them over the local network: browse artists, albums, tracks, playlists,
artwork, waveforms, and lyrics, and stream audio with authenticated URLs that
work directly in FFmpeg and browsers. It runs as a plain binary or a systemd
service, and every release ships DEB/RPM packages. The
[Wiki](https://github.com/bschlaack/Orynivo/wiki) and the
[full reference](docs/FULL-FEATURE-REFERENCE.md#orynivo-server) document the
complete REST API and setup.

## Supported formats

`DSF`, `DFF`, `FLAC`, `MP3`, `WAV`, `AIFF`, `M4A`, `MKA` (with chapters),
`AAC`, `OGG`, `Opus`, `WMA`, and CUE sheets referencing PCM sources. PCM is
decoded via FFmpeg, which Orynivo locates or downloads automatically.

## Documentation

- [GitHub Wiki](https://github.com/bschlaack/Orynivo/wiki) — end-user guides
- [Full feature & technical reference](docs/FULL-FEATURE-REFERENCE.md) — every
  feature, output profile, format behavior, and build detail
- [CHANGELOG.md](CHANGELOG.md) · [ROADMAP.md](ROADMAP.md)

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```bash
dotnet restore Orynivo/Orynivo.csproj
dotnet build Orynivo/Orynivo.csproj --configuration Release
dotnet run --project Orynivo/Orynivo.csproj
```

`./build.ps1` runs the full per-platform build. Platform-specific
prerequisites (FFmpeg, ALSA/OpenAL, ASIO SDK) and packaging commands are
described in the
[full reference](docs/FULL-FEATURE-REFERENCE.md#build).

## Contributing

Issues, feature requests, and pull requests are welcome — please target the
`development` branch. See [CONTRIBUTING.md](CONTRIBUTING.md) for the dev
setup, test suite, and what the maintainers look for.

## License

Orynivo is licensed under the [Apache License 2.0](LICENSE). Third-party
components are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and
[`licenses/`](licenses/).

> Orynivo is under active development. The database schema, user interface, and
> available features may still change between releases.
