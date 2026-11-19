# Rimlight

**Your screen edges glow with your music.** Rimlight draws a soft neon light around the edges of your screen that moves to the beat of whatever you're playing (Spotify, YouTube, Apple Music…), takes its colors from the album cover, and can show synced lyrics in style.

Free, for Windows 10 and 11.

## Download & install (2 minutes)

1. Go to **[Releases](https://github.com/nuralmahtab1-glitch/rimlight/releases/latest)** and download **`Rimlight-win-Setup.exe`**.
2. Double-click it. No admin needed.
3. **"Windows protected your PC"?** That's because Rimlight is a small free app that isn't code-signed yet. Click **More info → Run anyway**.
   - If Windows says *"Smart App Control blocked this"*, there's no Run-anyway button. You'd need to turn Smart App Control off (Settings → Privacy & security → Windows Security → App & browser control).
4. Play something in Spotify (or any music app) and watch your screen edges.

Rimlight **updates itself**. When a new version is out you'll get a notification, and it installs the next time Rimlight starts.

Prefer no installer? Download **`Rimlight-win-Portable.zip`**, unzip, run `Rimlight.exe` (no auto-updates).

## Using it

Rimlight lives in the **system tray** (bottom-right, next to the clock; click the **^** arrow if you don't see it). Tip: drag the icon out of the ^ menu onto the taskbar so it's always visible.

- **Click the tray icon** → settings (colors, motion patterns, lyrics styles, brightness…)
- **Right-click** → quick toggles: light, lyrics, lyrics over apps, quit

| Shortcut | Does |
|---|---|
| **Ctrl+Alt+L** | Light on/off |
| **Ctrl+Alt+K** | Lyrics on/off |
| **Ctrl+Alt+O** | Open/close settings |

## Troubleshooting

- **No album colors?** Check your music shows up in the Windows media popup (press a volume key).
- **Glow not reacting to music?** Make sure the right speakers/headphones are selected in Windows sound settings.
- **No lyrics for a song?** Lyrics come from the free LRCLIB database; less-known songs may not have them.
- **Something broke?** Settings → **Open logs** and send the log file.

## Uninstall

Settings → Apps → Installed apps → **Rimlight** → Uninstall.

## Privacy

Rimlight never uses your microphone and never records audio: it listens to what your PC plays, analyses it in memory and throws it away. The only internet use: checking GitHub for updates, and (if lyrics are on) looking up the current song's title/artist on lrclib.net.

---

## For developers

C# / .NET 10, WPF + Direct3D 11 / DirectComposition overlay, NAudio (WASAPI loopback), Velopack.

```powershell
dotnet test tests/Rimlight.Tests
.\build.ps1 -Version 1.0.1            # builds dist\Rimlight-win-Setup.exe + portable zip
.\build.ps1 -Version 1.0.1 -Publish   # also publishes a GitHub release (friends auto-update)
```

MIT licensed.
