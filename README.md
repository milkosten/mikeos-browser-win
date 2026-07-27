# MikeBrowser for Windows

Native **Windows 11** MikeBrowser — a **WinUI 3** shell around a **WebView2** content engine, with a
**Chrome-style profile sign-in** to your `osmike.com` account so your **bookmarks sync** with your phone
(via `mikeos-browser-cloud`).

- **Download:** https://browser.osmike.com
- **Design & decisions:** [PLAN.md](PLAN.md)
- **Sibling (Android):** `mikeos-browser` (the phone app-agent + the same cloud)

## Status
Early scaffold. The Windows app is built/tested on a Windows 11 VM (see the `winbench` env on the media
box) because WinUI 3 can't be built on Linux.

## Repo layout
```
src/     MikeBrowserWin — the WinUI 3 app (.NET 8, win-x64 self-contained)
web/     browser.osmike.com landing/download page (static, served by the box Caddy)
docs/    notes
```

## Build (inside the Windows VM)
```powershell
dotnet build src/MikeBrowserWin/MikeBrowserWin.csproj -c Release
dotnet publish src/MikeBrowserWin/MikeBrowserWin.csproj -c Release -r win-x64 --self-contained
```
