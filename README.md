# Rust Skin Viewer

## Workflow Status

| Workflow | Status                                                                                                                                                                                                |
| -------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Prices   | [![Prices](https://github.com/tsgsOFFICIAL/rust-skin-archives/actions/workflows/update-prices.yml/badge.svg)](https://github.com/tsgsOFFICIAL/rust-skin-archives/actions/workflows/update-prices.yml) |

Last price update: 2026-10-02 00:10 UTC

Last full asset update: 2026-09-25 15:03 UTC

A searchable archive of Rust workshop skins with an in-browser 3D viewer and prices from 16 markets.

## Features

- **Search and filters:** free-text search (`type:`, `name:`, `id:` also work), min/max price, a multi-select item type dropdown, and Glow / Cutout / Twitch toggles. The filters are kept in the URL, so a filtered view can be shared.
- **Sorting:** newest first by default, or A-Z, Z-A and price.
- **Prices:** the skin popup lists every market's lowest price, cheapest first with a BEST badge, and each row links to that market.
- **3D viewer:** rotate and zoom any skin's model, with day and night lighting.
- **Random skin:** a spinning wheel that picks from the current filters.

## Layout

- `docs/` is the website (static files, GitHub Pages serves it from `main`): `index.html`, `js/`, `css/`, `skins.json`, `prices.json`.
    - `docs/models/` holds one `<skinId>.glb` per skin and `docs/icons/` one `<skinId>.png` icon. Both are stored with Git LFS.
- `pipeline/` builds everything the site needs.
    - `RustSkinToGlb/` converts a skin's textures and the game mesh into a GLB, and runs the weekly update (`--weekly`).
    - `PriceFetcher/` fetches prices for every skin from [SCMM](https://rust.scmm.app/docs) (Steam, Skinport, CS.Deals, DMarket and more, one request) into `docs/prices.json`. A GitHub Action (`.github/workflows/update-prices.yml`) runs it every hour. The site builds each market's link from the skin name in `docs/js/market-urls.js`.
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

## Prices

All prices come from [SCMM](https://rust.scmm.app) in a single request, every hour, by a GitHub Action. Steam, Skinport, CS.Deals, DMarket, Rust.tm, Waxpeer, Lis-Skins, Mannco, Avan Market, CS.Trade, ShadowPay, Swap.gg, Tradeit.gg, SkinSwap, RapidSkins and Loot.farm are covered. Skins are matched by exact name, so the few names shared by several skins get no prices.

SCMM keeps serving a market's last prices even after it stops updating them, so any offer it hasn't seen for 24 hours is dropped (`MaxOfferAge` in `Scmm.cs`). A market that has gone stale therefore disappears from the site until SCMM updates it again.

`docs/prices.json` has a `prices` array per skin, one entry per market. `price` is USD cents (the lowest listing), and `sources` records when each source was fetched:

```json
{
    "version": 2,
    "sources": { "scmm": { "fetchedAt": "2026-09-28T08:48:33Z", "complete": true } },
    "items": {
        "489329801": { "prices": [{ "name": "Steam", "price": 32, "listings": 68 }] }
    }
}
```

The file contains no links. The site builds each market's link from the skin's name in `docs/js/market-urls.js`.

**Adding a market:**

1. Add it to the `Markets` list in `pipeline/PriceFetcher/Scmm.cs` (SCMM's market id and the name to show).
2. Add a link builder under that same name in `docs/js/market-urls.js`.

**Run it locally** (needs only the .NET 8 SDK, no API key). It only writes `docs/prices.json` and never commits, so use `git restore docs/prices.json` afterwards if you don't want to keep the result:

```bash
dotnet run --project pipeline/PriceFetcher -c Release -- --skins docs/skins.json --out docs/prices.json
```

If SCMM is unreachable or returns too little data, nothing is written and the exit code is 2.

## Credits

- Prices from [SCMM](https://rust.scmm.app), a fan-made Rust market tool. Thank you for the free API!
- Skin data comes from Steam and the game files. This is a fan project and is not affiliated with Facepunch or Valve.
