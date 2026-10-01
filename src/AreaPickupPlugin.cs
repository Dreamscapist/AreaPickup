using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace AreaPickup
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.NotEnforced, VersionStrictness.None)]
    internal class AreaPickupPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "dreamscapist.valheim.areapickup";
        public const string PluginName = "AreaPickup";
        public const string PluginVersion = "1.0.1";

        private const float DefaultRange = 10f;
        private const float MinRange = 5f;
        private const float MaxRange = 100f;

        // Remote-owned drops need an ownership handoff before Humanoid.Pickup will accept them.
        // ItemDrop.RequestOwn throttles itself to one request per 0.2 s, so retry just above that.
        private const int OwnershipRetries = 4;
        private const float OwnershipRetryDelay = 0.3f;

        // Harvested pickables spawn their item at the plant, which is usually outside vanilla
        // auto-pickup reach — wait for those drops to exist, then sweep them up too.
        private const float PickableDropDelay = 0.6f;

        // Read by PlayerMessagePatch to silence the per-item "$msg_added" spam during a sweep.
        internal static bool SuppressTopLeftMessages;

        private ConfigEntry<float> _range;
        private ConfigEntry<KeyboardShortcut> _hotkey;
        private ConfigEntry<bool> _pickupGroundItems;
        private ConfigEntry<bool> _harvestPickables;
        private ConfigEntry<bool> _includeFish;
        private ConfigEntry<bool> _ignorePlacedItems;
        private ConfigEntry<bool> _debugLogging;
        private ConfigEntry<string> _blacklistEntry;
        private ConfigEntry<bool> _showPerItemMessages;
        private ConfigEntry<bool> _showSummary;

        private readonly HashSet<string> _blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private ButtonConfig _pickupButton;
        private Harmony _harmony;
        private bool _busy;

        private void Awake()
        {
            BindConfig();

            _pickupButton = new ButtonConfig
            {
                Name = "AreaPickup_Pickup",
                ShortcutConfig = _hotkey
            };
            InputManager.Instance.AddButton(PluginGUID, _pickupButton);

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll(typeof(AreaPickupPlugin).Assembly);

            Jotunn.Logger.LogInfo($"{PluginName} v{PluginVersion} loaded");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        private void BindConfig()
        {
            _range = Config.Bind("General", "Range", DefaultRange,
                new ConfigDescription("Pickup radius in meters.",
                    new AcceptableValueRange<float>(MinRange, MaxRange)));

            _hotkey = Config.Bind("General", "Hotkey",
                new KeyboardShortcut(KeyCode.E, KeyCode.LeftShift),
                "Key combination that picks up everything in range.");

            _pickupGroundItems = Config.Bind("Filters", "PickupGroundItems", true,
                "Pick up dropped items lying on the ground.");

            _harvestPickables = Config.Bind("Filters", "HarvestPickables", false,
                "Harvest pickables in range (berries, mushrooms, stones, flint, branches, grown crops, ...).");

            _includeFish = Config.Bind("Filters", "IncludeFish", false,
                "Also grab fish. Off by default so live fish can't be scooped out of the water.");

            _ignorePlacedItems = Config.Bind("Filters", "IgnorePlacedItems", true,
                "Leave placed-down items alone (food set out on tables, decorative items). " +
                "Only loose, dropped items get picked up.");

            _debugLogging = Config.Bind("Debug", "LogCandidates", false,
                "Log every item found in range and why it was picked up or skipped. " +
                "Use this if something placed still gets grabbed, or a dropped item gets left behind.");

            _blacklistEntry = Config.Bind("Filters", "Blacklist", "",
                "Comma-separated prefab names to ignore, e.g. Pickable_Carrot,Wood,Resin");
            _blacklistEntry.SettingChanged += (_, __) => ParseBlacklist();
            ParseBlacklist();

            _showPerItemMessages = Config.Bind("Messages", "ShowPerItemMessages", false,
                "Show vanilla's top-left 'added X' message for every item picked up.");

            _showSummary = Config.Bind("Messages", "ShowSummary", true,
                "Show one summary message after each sweep.");
        }

        private void ParseBlacklist()
        {
            _blacklist.Clear();
            foreach (string raw in _blacklistEntry.Value.Split(','))
            {
                string name = raw.Trim();
                if (name.Length > 0) _blacklist.Add(name);
            }
        }

        private void Update()
        {
            if (_busy || ZInput.instance == null) return;
            if (!ZInput.GetButtonDown(_pickupButton.Name)) return;

            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead() || player.IsTeleporting() || player.InCutscene()) return;
            if (!player.TakeInput()) return; // chat, console, menus, inventory open, etc.

            StartCoroutine(PickupRoutine(player));
        }

        private IEnumerator PickupRoutine(Player player)
        {
            _busy = true;
            try
            {
                float range = Mathf.Clamp(_range.Value, MinRange, MaxRange);
                int harvested = 0;
                int collected = 0;
                bool inventoryFull = false;

                if (_harvestPickables.Value)
                {
                    harvested = HarvestPickables(player, range);
                    if (harvested > 0 && _pickupGroundItems.Value)
                        yield return new WaitForSeconds(PickableDropDelay);
                }

                if (_pickupGroundItems.Value)
                {
                    for (int attempt = 0; attempt <= OwnershipRetries; attempt++)
                    {
                        if (player == null || player.IsDead()) yield break;

                        SweepResult sweep = SweepItemDrops(player, range);
                        collected += sweep.Collected;
                        inventoryFull |= sweep.InventoryFull;

                        if (sweep.Pending == 0) break;
                        yield return new WaitForSeconds(OwnershipRetryDelay);
                    }
                }

                if (_showSummary.Value && player != null)
                    player.Message(MessageHud.MessageType.TopLeft, BuildSummary(collected, harvested, inventoryFull));
            }
            finally
            {
                SuppressTopLeftMessages = false;
                _busy = false;
            }
        }

        private int HarvestPickables(Player player, float range)
        {
            Vector3 center = player.transform.position;
            int harvested = 0;

            SuppressTopLeftMessages = !_showPerItemMessages.Value;
            try
            {
                foreach (Pickable pickable in FindInRange<Pickable>(center, range))
                {
                    if (pickable.m_picked || !HasValidView(pickable) || IsBlacklisted(pickable.gameObject)) continue;
                    if (pickable.Interact(player, false, false)) harvested++;
                }

                foreach (PickableItem pickableItem in FindInRange<PickableItem>(center, range))
                {
                    if (!HasValidView(pickableItem) || IsBlacklisted(pickableItem.gameObject)) continue;
                    if (pickableItem.Interact(player, false, false)) harvested++;
                }
            }
            finally
            {
                SuppressTopLeftMessages = false;
            }

            return harvested;
        }

        private SweepResult SweepItemDrops(Player player, float range)
        {
            var result = new SweepResult();
            Inventory inventory = player.GetInventory();

            SuppressTopLeftMessages = !_showPerItemMessages.Value;
            try
            {
                foreach (ItemDrop drop in FindInRange<ItemDrop>(player.transform.position, range))
                {
                    if (drop == null) continue;

                    ZNetView view = drop.GetComponent<ZNetView>();
                    if (view == null || !view.IsValid()) continue;
                    if (IsBlacklisted(drop.gameObject)) continue;
                    if (!_includeFish.Value && drop.GetComponent<Fish>() != null) continue;

                    drop.Load();
                    if (drop.m_itemData == null) continue;

                    if (_ignorePlacedItems.Value && IsPlaced(drop, out string reason))
                    {
                        if (_debugLogging.Value)
                            Jotunn.Logger.LogInfo($"[AreaPickup] skip placed {PrefabName(drop.gameObject)} ({reason})");
                        continue;
                    }

                    if (_debugLogging.Value)
                        Jotunn.Logger.LogInfo($"[AreaPickup] take {PrefabName(drop.gameObject)} x{drop.m_itemData.m_stack} " +
                                              $"(autoPickup={drop.m_autoPickup}, kinematic={IsKinematic(drop)}, " +
                                              $"food={drop.m_itemData.m_shared.m_food})");

                    if (!inventory.CanAddItem(drop.m_itemData))
                    {
                        result.InventoryFull = true;
                        continue;
                    }

                    if (!view.IsOwner())
                    {
                        drop.RequestOwn();
                        result.Pending++;
                        continue;
                    }

                    int stack = drop.m_itemData.m_stack;
                    if (player.Pickup(drop.gameObject, false, false))
                        result.Collected += stack;
                }
            }
            finally
            {
                SuppressTopLeftMessages = false;
            }

            return result;
        }

        private static List<T> FindInRange<T>(Vector3 center, float range) where T : Component
        {
            var seen = new HashSet<T>();
            var found = new List<T>();

            foreach (Collider collider in Physics.OverlapSphere(center, range, Physics.AllLayers, QueryTriggerInteraction.Collide))
            {
                T component = collider.GetComponentInParent<T>();
                if (component != null && seen.Add(component)) found.Add(component);
            }

            // Nearest first, so a nearly full inventory fills with what's closest.
            found.Sort((a, b) =>
                (a.transform.position - center).sqrMagnitude.CompareTo((b.transform.position - center).sqrMagnitude));
            return found;
        }

        private static bool HasValidView(Component component)
        {
            ZNetView view = component.GetComponent<ZNetView>();
            return view != null && view.IsValid();
        }

        // A loose drop is a physics object that vanilla would auto-pickup. Something set down on
        // purpose fails at least one of these: vanilla flags it as not auto-pickupable, it is (or
        // belongs to) a built Piece, or it has no live rigidbody because it's pinned in place.
        private static bool IsPlaced(ItemDrop drop, out string reason)
        {
            if (!drop.m_autoPickup)
            {
                reason = "autoPickup=false";
                return true;
            }

            if (drop.GetComponentInParent<Piece>() != null)
            {
                reason = "part of a piece";
                return true;
            }

            if (IsKinematic(drop))
            {
                reason = "no free-moving rigidbody";
                return true;
            }

            reason = null;
            return false;
        }

        private static bool IsKinematic(ItemDrop drop)
        {
            Rigidbody body = drop.GetComponent<Rigidbody>();
            return body == null || body.isKinematic;
        }

        private bool IsBlacklisted(GameObject go)
        {
            return _blacklist.Count > 0 && _blacklist.Contains(PrefabName(go));
        }

        private static string PrefabName(GameObject go)
        {
            string name = go.name;
            int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone >= 0) name = name.Substring(0, clone);
            return name.Trim();
        }

        private static string BuildSummary(int collected, int harvested, bool inventoryFull)
        {
            string text;
            if (collected == 0 && harvested == 0)
                text = "Nothing to pick up";
            else if (harvested > 0)
                text = $"Picked up {collected} item(s), harvested {harvested}";
            else
                text = $"Picked up {collected} item(s)";

            if (inventoryFull) text += " — inventory full, some left behind";
            return text;
        }

        private class SweepResult
        {
            public int Collected;
            public int Pending;
            public bool InventoryFull;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.Message))]
    internal static class PlayerMessagePatch
    {
        private static bool Prefix(MessageHud.MessageType __0)
        {
            return !(AreaPickupPlugin.SuppressTopLeftMessages && __0 == MessageHud.MessageType.TopLeft);
        }
    }
}
