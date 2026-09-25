# Rust Skin Viewer

## Workflow Status

| Workflow | Status                                                                                                                                                                                                |
| -------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Prices   | [![Prices](https://github.com/tsgsOFFICIAL/rust-skin-archives/actions/workflows/update-prices.yml/badge.svg)](https://github.com/tsgsOFFICIAL/rust-skin-archives/actions/workflows/update-prices.yml) |

Last price update: 2026-09-25 14:19 UTC

Last full asset update: 2026-09-24

A searchable archive of Rust workshop skins with an in-browser 3D viewer.

- `docs/` is the website (static files, GitHub Pages serves it from `main`): `index.html`, `js/`, `css/`, `skins.json`, `prices.json`.
    - `docs/models/` holds one `<skinId>.glb` per skin and `docs/icons/` one `<skinId>.png` icon. Both are stored with Git LFS.
- `pipeline/` builds everything the site needs.
    - `RustSkinToGlb/` converts a skin's textures and the game mesh into a GLB, and runs the weekly update (`--weekly`).
    - `PriceFetcher/` fetches Steam Community Market prices into `docs/prices.json`. A GitHub Action (`.github/workflows/update-prices.yml`) runs it every 8 hours.
    - `data/` keeps the skin list (`data.json`), the Steam item catalog and the per-skin flag cache. The extracted textures in `data/all-skins-textures/` are large and are not committed.
    - `tools/weekly-update.ps1` is the weekly entry point (new skins, GLBs, `skins.json`, icons).

## Requirements (weekly update, on a Windows machine)

.NET 8 SDK, SteamCMD (anonymous login) and a Steam Web API key in the `STEAM_API_KEY` environment variable.
The key is only read from that variable and is never written to disk or committed.

## Weekly update

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File pipeline\tools\weekly-update.ps1
```

Add `-DryRun` to only report what is new.
