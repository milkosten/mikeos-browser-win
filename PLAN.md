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

## Auth flow — Chrome-style profile sign-in (VALIDATED end-to-end on the LIVE IdP, 2026-07-28)
The OAuth 2.0 AS in `ACCOUNT-OSMIKE-OAUTH-PLAN.md` isn't built yet, so v1 drives the live IdP. The
user only types **email + password once**; everything below is programmatic (no browser round-trip,
no code to read). All calls to `https://account.osmike.com`:

1. `POST /api/auth/login {email,password}` → `{ token: <JWT>, user:{id} }`.  *(the only human step)*
2. Generate a per-machine `deviceId` (UUID, persisted). `POST /api/devices/pair/request {deviceId, deviceName}` *(no auth)* → `{ code, activationUrl }`.
3. **Auto-approve with the JWT** (the app is the logged-in user): `POST /api/devices/pair/activate` *(Bearer JWT)* `{ code, mode:"new_slot", slot_name:"<hostname>" }` → `{ device_id, session_token }` (the **canonical** device_id; may differ from the requested one — use the returned one).
   - Re-sign-in on the same PC: the slot already exists → `new_slot` 409s ("slot with that name"); handle by `GET /api/devices/slots` then `mode:"claim_existing", existing_device_id/slot_id`. Use the machine hostname (or a stored slot_id) as the slot name so re-login is idempotent.
4. `POST /api/mikeos/agents {deviceId, app:"MikeBrowser"}` *(no auth — a linked device_id IS the credential)* → `{ agent_key, name:"<user>/<device>/MikeBrowser", user_id }`.
5. Persist `{ user_id, deviceId, slot_id, agent_key }` via **DPAPI** (encrypted at rest). All
   `mikeos-browser-cloud` calls carry `X-API-KEY: agent_key`.
6. Bookmarks/history now sync with the phone (same `user_id`). **Proven:** the minted key read
   Mike's live bookmark from browser-cloud; cross-device sync + account isolation already verified on
   the Android bench.
7. **Seam for the future:** when the OAuth AS ships, swap steps 1–4 for "Sign in with MikeOS"
   (Auth Code + PKCE) → Bearer JWT; browser-cloud plans dual-auth. Isolated in `AccountClient`.

> Endpoints confirmed against the live IdP (`mikeoscomputers`, repo cloned at
> `/home/mikeos/projects/mikeoscomputers`). `pair/request` is unauth; `pair/activate` needs the JWT;
> `/api/mikeos/agents` needs only a linked `deviceId`.

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
