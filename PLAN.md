# MikeBrowser for Windows — plan

A native **Windows 11** build of MikeBrowser: a **WinUI 3 shell + WebView2 content engine** (mirrors
the MikeOS "native shell + WebView content engine" contract). Downloadable from
**https://browser.osmike.com**. Sign in to a **browser profile from your osmike.com account** (Chrome-style)
and your **bookmarks sync** with the phone via `mikeos-browser-cloud`.

## Decisions (locked with the owner 2026-07-27)
- **UI stack:** WinUI 3 (Windows App SDK) + WebView2, .NET 8 (`net8.0-windows10.0.19041.0`), self-contained `win-x64` (no .NET install needed by the user).
- **Login:** a **browser-profile sign-in like Chrome** — email + password against `account.osmike.com`, no device pairing UX, no tokens shown. Under the hood it obtains a **user-scoped key** and syncs.
- **Build/test env:** Windows 11 VM on the media box (`91.98.177.242`) via `dockurr/windows` (KVM, no host reboot), disk on RAID6 `/data/mikeos-winbench`. See memory `winbench-windows-vm`.
- **Distribution:** new repo `mikeos-browser-win`; `browser.osmike.com` served from the box (Caddy `deploy-caddy-1`, DNS-only grey-cloud) — landing page + installer file both served from the box.

## Architecture
```
MikeBrowserWin (WinUI 3, .NET 8, win-x64 self-contained)
├─ Shell (XAML): address bar (omnibox) + WebView2 + bookmarks
│   └─ ports the Android omnibox UX: tap-to-select-all, clear, URL-vs-search, session restore
├─ Bookmarks: star toggle + bookmarks flyout  ──┐
├─ Services/BrowserCloudClient  ────────────────┼─▶ mikeos-browser-cloud (Railway)
│     GET/POST/DELETE /api/bookmarks, /api/history   X-API-KEY = the profile's user key
├─ Services/AccountClient  ─────────────────────▶ account.osmike.com (IdP, live)
│     POST /api/auth/login (email+pw) → JWT → obtain user-scoped browser key
└─ Services/SessionStore: persist the profile creds encrypted at rest (Windows DPAPI)
```

## Auth flow (Chrome-style profile sign-in, on the LIVE IdP — no OAuth AS needed yet)
The OAuth 2.0 AS in `ACCOUNT-OSMIKE-OAUTH-PLAN.md` is not built yet, so v1 uses the live IdP:
1. User clicks **Sign in** → enters email + password (their osmike.com account).
2. App `POST account.osmike.com/api/auth/login` → **JWT** (30-day).
3. App obtains a **user-scoped key** for browser-cloud. **[TO VALIDATE against the live IdP]** — the
   two candidate paths, pick whichever the IdP supports cleanly:
   - **a. Per-user key:** `POST/GET /api/keys` (JWT-auth) → a user key that browser-cloud's `/resolve` maps to `user_id`.
   - **b. Device+agent:** register this PC as a device (`/api/devices/pair`) → mint a `MikeBrowser` agent key (`POST /api/mikeos/agents`). More faithful to the phone model; gives the PC a `device_id`.
4. Store `{ user_id, key, refresh/JWT }` via **DPAPI**; browser-cloud calls carry `X-API-KEY: <key>`.
5. Bookmarks/history now sync with the phone (same `user_id`). Verified cross-device+isolation on the
   Android test bench already.
6. **Seam for the future:** when the OAuth AS ships, replace steps 1–3 with "Sign in with MikeOS"
   (Auth Code + PKCE) → Bearer JWT; browser-cloud already plans dual-auth. One-file change.

> Open item requiring the owner or a test account: validate step 3 against the live IdP (need a
> login that isn't Mike's real password, or Mike runs the one call). Until then the sync client is
> testable directly with a known agent key.

## Milestones
1. **VM up** (done — installing) → install .NET 8 SDK + WinUI 3 + WebView2 + VS Build Tools in the guest.
2. **App v0:** WinUI 3 shell + WebView2 + omnibox (no account) → browses. Build + smoke-test in the VM.
3. **Sync:** BrowserCloudClient + bookmarks star/flyout against a known key → prove sync vs the phone.
4. **Profile login:** AccountClient (validate step 3) → real Chrome-style sign-in.
5. **Installer:** package (unpackaged win-x64 + Inno Setup `MikeBrowserSetup.exe`, or MSIX).
6. **Distribution:** `browser.osmike.com` DNS (Cloudflare, DNS-only) + Caddy site block on the box →
   landing page + installer download.

## Non-goals (v1)
- The on-device daemon / hive / heartbeat / cross-app hand-offs (no MikeOS daemon on a bare Windows PC).
  v1 is the browser + profile sync. Copilot/agent features are a later phase.
