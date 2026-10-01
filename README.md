# AreaPickup

Pick up everything around you with one key press.

Press **Shift + E** and AreaPickup grabs every dropped item within range. Food you've set out on tables and other placed items stay where they are. Optionally, it can also harvest nearby berries, mushrooms, stones, flint and crops.

## Features

- One key press picks up everything in a radius (default **10 m**, adjustable **5–100 m**)
- Leaves placed-down food and decorative items alone
- Optional: harvest pickables and collect their drops in the same press (off by default)
- Nearest items first, so a nearly full inventory keeps what's closest
- Works in multiplayer: items owned by other players are requested and picked up automatically
- One summary message instead of a wall of "added X" spam
- Blacklist for anything you'd rather leave on the ground
- Live fish are ignored by default

## Configuration

Edit in r2modman's config editor, or `BepInEx/config/dreamscapist.valheim.areapickup.cfg`. Changes apply live.

| Section | Setting | Default | Description |
|---|---|---|---|
| General | Range | 10 | Pickup radius in meters (5–100) |
| General | Hotkey | LeftShift + E | Key combination that triggers a pickup |
| Filters | PickupGroundItems | true | Pick up dropped items |
| Filters | HarvestPickables | false | Harvest berries, mushrooms, crops, stones, etc. |
| Filters | IncludeFish | false | Also grab fish |
| Filters | IgnorePlacedItems | true | Leave placed-down food and decor alone |
| Filters | Blacklist | *(empty)* | Comma-separated prefab names to skip, e.g. `Pickable_Carrot,Resin` |
| Messages | ShowPerItemMessages | false | Show vanilla's message for every item |
| Messages | ShowSummary | true | Show one summary after each pickup |
| Debug | LogCandidates | false | Log every item in range and why it was taken or skipped |

## Notes

- **Shift + E also triggers the normal "Use" action** on whatever you're looking at (E is vanilla's interact key). If that gets in your way, rebind the hotkey.
- With HarvestPickables on, ripe crops in range get harvested too, including carrots and turnips you might be growing for seeds. Blacklist their `Pickable_*` names (e.g. `Pickable_Carrot`) to leave them standing.
- Client-side only; other players don't need it installed.

## Requirements

- BepInExPack Valheim
- Jötunn

## Changelog

**1.0.1** — Placed-down food and decor are no longer picked up. HarvestPickables is now off by default. Added a debug log option.

**1.0.0** — Initial release.
