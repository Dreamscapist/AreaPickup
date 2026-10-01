# Building AreaPickup

## Requirements

- .NET SDK (6.0 or newer)
- Valheim at `D:\Steam\steamapps\common\Valheim`
- r2modman profile `DreamHeim2` with BepInExPack and Jötunn (`ValheimModding-Jotunn`) installed

Paths are set at the top of `AreaPickup.csproj` (`ValheimDir`, `ProfileDir`). Change them there if your install moves.

## Build

```
dotnet build -c Release
```

The DLL is copied automatically to
`DreamHeim2\BepInEx\plugins\Dreamscapist-AreaPickup\AreaPickup.dll`.

Expect harmless warnings about `System.Net.Http` / `System.IO.Compression` version conflicts (netstandard2.1 vs Valheim's .NET 4.8 assemblies).

## Releasing

1. Bump the version in **both** `PluginVersion` in `src/AreaPickupPlugin.cs` and `<Version>` in the csproj — otherwise BepInEx can keep loading a stale DLL.
2. Update `version_number` in `manifest.json` and the changelog in `README.md`.
3. Zip for Thunderstore: `manifest.json`, `README.md`, `icon.png` (256×256), and `AreaPickup.dll`, all at the zip root.

## How it works

- **Input:** a Jötunn `ButtonConfig` bound to the `Hotkey` config entry, polled with `ZInput.GetButtonDown` in `Update()`. Ignored while `Player.TakeInput()` is false (chat, console, menus, inventory).
- **Discovery:** one `Physics.OverlapSphere` at the player (all layers, triggers included), `GetComponentInParent<T>()` on each hit, de-duplicated and sorted nearest first.
- **Pickables:** `Pickable.Interact` / `PickableItem.Interact` — the vanilla RPC path, so remote-owned plants work.
- **Ground items:** `Humanoid.Pickup(go, autoequip: false, autoPickupDelay: false)`. `Pickup` requires ZDO ownership, so for remote-owned drops the sweep calls `ItemDrop.RequestOwn()` and retries up to 4× at 0.3 s (RequestOwn self-throttles to 0.2 s).
- **Placed items:** with `IgnorePlacedItems` on, an `ItemDrop` is skipped when `m_autoPickup` is false (the flag vanilla's own auto-pickup respects), when it sits under a `Piece`, or when its `Rigidbody` is missing or kinematic. `Debug.LogCandidates` logs each decision with those values.
- **Harvest drops:** after harvesting, waits 0.6 s for the spawned items to exist, then sweeps.
- **Message spam:** a Harmony prefix on `Player.Message` swallows `TopLeft` messages while a sweep is running; a single summary is shown at the end.
