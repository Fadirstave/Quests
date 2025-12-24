using System;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Oxide.Plugins
{
    [Info("Quests", "FadirStave", "2.0.0")]
    public class Quests : RustPlugin
    {
        // =========================
        // LOGIN / IGNORE (INFOPANEL-STYLE)
        // =========================
        private const string IgnoreDataFile = "Quests_Ignore";
        private Dictionary<ulong, bool> ignoredPlayers = new Dictionary<ulong, bool>();
        private readonly HashSet<ulong> loginShownThisSession = new HashSet<ulong>();

        // =========================
        // MODELS
        // =========================
        class QuestReward
        {
            public string ShortName;
            public int Amount;
        }

        class QuestDefinition
        {
            public int Id;
            public string Title;
            public string Description;
            public Dictionary<string, int> Requirements;
            public List<QuestReward> Rewards;
            public bool HasInventoryTrackedRequirements;
        }

        class QuestProgress
        {
            public int QuestId;
            public Dictionary<string, int> Progress = new Dictionary<string, int>();
            public bool Completed;
            public bool RewardPending;
            public bool Started;
        }

        private static readonly Dictionary<string, string> RewardShortNameAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["teas.wood"] = "woodtea",
            ["teas.ore"] = "oretea",
            ["tea.wood"] = "woodtea",
            ["tea.ore"] = "oretea",
            ["teawood"] = "woodtea",
            ["teaore"] = "oretea",
            ["basic wood tea"] = "woodtea",
            ["basic ore tea"] = "oretea",
            ["basic.wood.tea"] = "woodtea",
            ["basic.ore.tea"] = "oretea",
            ["wood tea"] = "woodtea",
            ["ore tea"] = "oretea",
            ["harvesting tea"] = "teabasic.pick",
            ["fish pie"] = "pie.fish",
            ["fishpie"] = "pie.fish",
            ["pie.fish"] = "pie.fish",
            ["boneknife"] = "knife.bone",
            ["advanced ore tea"] = "oretea.advanced",
            ["oretea.adv"] = "oretea.advanced",
            ["advancedoretea"] = "oretea.advanced",
            ["basicblueprintfragment"] = "blueprint.fragment.basic",
            ["basic blueprint fragment"] = "blueprint.fragment.basic",
            ["basic blueprint"] = "blueprint.fragment.basic",
            ["wall.torch"] = "torchholder",
            ["torch holder"] = "torchholder",
            ["torchholder"] = "torchholder",
        };

        private static readonly HashSet<string> InventoryTrackedRequirementKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "metal.ore",
            "sulfur.ore",
            "metal.fragments",
            "scrap",
            "lowgradefuel",
            "cloth",
        };

        // =========================
        // DATA
        // =========================
        private Dictionary<int, QuestDefinition> quests;
        private Dictionary<ulong, QuestProgress> activeQuests;
        private readonly Dictionary<ItemCraftTask, ulong> craftTaskOwners = new Dictionary<ItemCraftTask, ulong>();
        private readonly HashSet<ulong> rewardDelayPending = new HashSet<ulong>();

        private const string PlayerDataFile = "Quests_PlayerData";

        // =========================
        // UI IDS
        // =========================
        private const string UiRoot = "QuestsUI.Root";
        private const string QuestBar = "QuestsUI.QuestBar";
        private const string Parchment = "QuestsUI.Parchment";
        private const string GoalBar = "QuestsUI.GoalBar";

        private const string QuestCompleteRoot = "QuestsUI.CompleteOverlay";
        private const string QuestCompleteTop = "QuestsUI.CompleteTop";
        private const string QuestCompleteParchment = "QuestsUI.CompleteParchment";
        private const string QuestCompleteBottom = "QuestsUI.CompleteBottom";

        private readonly HashSet<ulong> questUiVisible = new HashSet<ulong>();
        private readonly HashSet<ulong> questCompleteVisible = new HashSet<ulong>();

        // =========================
        // UI CONSTANTS
        // =========================
        private const int MaxCharsPerLine = 85;
        private const int MaxLines = 2;

        private const float QuestBarHeight = 0.12f;
        private const float GoalBarHeight = 0.10f;
        private const float LineHeight = 0.055f;
        private const float HotbarOffsetY = -0.085f;

        private const int QuestFontSize = 14;
        private const int BodyFontSize = 11;
        private const int GoalFontSize = 12;

        private const float UiAlpha = 0.65f;
        private const float UiSwapDelay = 0.6f;
        private const float RewardDelaySeconds = 1f;

        // =========================
        // CHAT
        // =========================
        private const string Prefix = "<color=#D87C2A>Quest:</color> ";
        private const string InvFullMsg =
            "Thy pack is full. Make room, then use /reward to claim thy due.";
        private const string DevPlaceholderMsg =
            "These quests are still in early devoplment and will added soon!";
        private const string MissingDescriptionFallback = "Description coming soon.";
        private const string EsquirePermission = "guishop.use";
        private const string EsquireChatPrefix = "<color=#D87C2A>[Esquire]</color> ";

        // =========================
        // LIFECYCLE
        // =========================
        private void OnServerInitialized()
        {
            LoadQuests();
            LoadPlayerData();
            LoadIgnoreData();
        }

        private void Init()
        {
            permission.RegisterPermission(EsquirePermission, this);
        }

        private object OnPlayerChat(BasePlayer player, string message, Chat.ChatChannel channel)
        {
            if (player == null || string.IsNullOrEmpty(message))
            {
                return null;
            }

            if (!permission.UserHasPermission(player.UserIDString, EsquirePermission))
            {
                return null;
            }

            if (message.StartsWith(EsquireChatPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            return EsquireChatPrefix + message;
        }

        private void Unload()
        {
            SavePlayerData();
            SaveIgnoreData();

            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, UiRoot);
                CuiHelper.DestroyUi(player, QuestCompleteRoot);
            }
        }

        private void OnServerSave()
        {
            SaveIgnoreData();
        }

        // =========================
        // ADMIN CHECK
        // =========================
        private bool IsAdmin(BasePlayer player) => player != null && player.IsAdmin;

        // =========================
        // LOGIN MESSAGE (ONCE PER CONNECTION)
        // =========================
        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;

            // Respect ignore
            if (ignoredPlayers.ContainsKey(player.userID)) return;

            // Once per connection
            if (loginShownThisSession.Contains(player.userID)) return;
            loginShownThisSession.Add(player.userID);

            // InfoPanel-style delay so chat is ready
            timer.Once(3f, () =>
            {
                if (player == null || !player.IsConnected) return;

                SendReply(player,
                    "<size=18><color=#D87C2A><b>MMO QUESTING</b></color></size>\n" +
                    "<color=#FFFFFF>This server features a unique MMO-style questing system.\n" +
                    "Use <b>/quest</b> to begin. Questing is optional and not required to play.</color>\n" +
                    "<color=#AAAAAA>Use <b>/questignore</b> to never see this message again. Report any issues on discord #bugreport.</color>"
                );
            });
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            // Allow showing again next time they connect (unless ignored)
            loginShownThisSession.Remove(player.userID);
        }

        [ChatCommand("questignore")]
        private void CmdQuestIgnore(BasePlayer player, string cmd, string[] args)
        {
            if (player == null) return;
            ignoredPlayers[player.userID] = true;
            SaveIgnoreData();

            SendReply(player, Prefix + "Quest intro hidden. (You can still use /quest anytime.)");
        }

        private void LoadIgnoreData()
        {
            ignoredPlayers =
                Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, bool>>(IgnoreDataFile)
                ?? new Dictionary<ulong, bool>();
        }

        private void SaveIgnoreData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(IgnoreDataFile, ignoredPlayers);
        }

        // =========================
        // QUEST DEFINITIONS
        // =========================
        private void LoadQuests()
        {
            quests = new Dictionary<int, QuestDefinition>();

            quests[1] = new QuestDefinition
            {
                Id = 1,
                Title = "Quest 1 – Getting Started",
                Description = "Take up thy rock and gather wood and stone, the first toil of any survivor.",
                Requirements = new Dictionary<string, int>
                {
                    ["wood"] = 400,
                    ["stones"] = 200
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "wood", Amount = 500 },
                    new QuestReward { ShortName = "stones", Amount = 500 }
                }
            };

            quests[2] = new QuestDefinition
            {
                Id = 2,
                Title = "Quest 2 — Stone Tools",
                Description = "Shape stone into tools, that thy labor be swifter and surer.",
                Requirements = new Dictionary<string, int>
                {
                    ["stonehatchet"] = 1,
                    ["stone.pickaxe"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "woodtea", Amount = 1 },
                    new QuestReward { ShortName = "oretea", Amount = 1 }
                }
            };

            quests[3] = new QuestDefinition
            {
                Id = 3,
                Title = "Quest 3 — Gather Supplies",
                Description = "Fill thy stores with wood and stone, for greater works soon follow.",
                Requirements = new Dictionary<string, int>
                {
                    ["wood"] = 2000,
                    ["stones"] = 2000
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "wood", Amount = 2000 },
                    new QuestReward { ShortName = "stones", Amount = 2000 }
                }
            };

            quests[4] = new QuestDefinition
            {
                Id = 4,
                Title = "Quest 4 — Builder’s Tools",
                Description = "Craft the tools of builders, and prepare to raise shelter.",
                Requirements = new Dictionary<string, int>
                {
                    ["building.planner"] = 1,
                    ["hammer"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "pie.fish", Amount = 1 }
                }
            };

            quests[5] = new QuestDefinition
            {
                Id = 5,
                Title = "Quest 5 — Claim and Build",
                Description = "Claim thy land and raise a humble shelter to rest within.",
                Requirements = new Dictionary<string, int>
                {
                    ["tc_auth"] = 1,
                    ["building_block"] = 4
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "sleepingbag", Amount = 1 }
                }
            };

            quests[6] = new QuestDefinition
            {
                Id = 6,
                Title = "Quest 6 — Storage",
                Description = "Build small boxes to guard thy goods from loss and decay.",
                Requirements = new Dictionary<string, int>
                {
                    ["box.wooden.crafted"] = 2
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "box.wooden.large", Amount = 1 }
                }
            };

            quests[7] = new QuestDefinition
            {
                Id = 7,
                Title = "Quest 7 — Secure the Door",
                Description = "Bar thy home with lock and door, and keep danger without.",
                Requirements = new Dictionary<string, int>
                {
                    ["door.hinged.wood"] = 1,
                    ["lock.key"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "torchholder", Amount = 1 },
                    new QuestReward { ShortName = "rug", Amount = 1 }
                }
            };

            quests[8] = new QuestDefinition
            {
                Id = 8,
                Title = "Quest 8 — Cloth Gathering",
                Description = "Gather cloth from field and foe, for craft and comfort.",
                Requirements = new Dictionary<string, int>
                {
                    ["cloth"] = 50
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "cloth", Amount = 50 }
                }
            };

            quests[9] = new QuestDefinition
            {
                Id = 9,
                Title = "Quest 9 — Simple Clothing",
                Description = "Stitch simple garb to shield thy flesh from harm.",
                Requirements = new Dictionary<string, int>
                {
                    ["burlap.shirt"] = 1,
                    ["burlap.trousers"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "wood.armor.jacket", Amount = 1 },
                    new QuestReward { ShortName = "wood.armor.pants", Amount = 1 }
                }
            };

            quests[10] = new QuestDefinition
            {
                Id = 10,
                Title = "Quest 10 — First Weapon",
                Description = "Craft bow and arrow, and learn to strike from afar.",
                Requirements = new Dictionary<string, int>
                {
                    ["bow.hunting"] = 1,
                    ["arrow.wooden"] = 20
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "knife.bone", Amount = 1 }
                }
            };

            quests[11] = new QuestDefinition
            {
                Id = 11,
                Title = "Quest 11 — Hunting",
                Description = "Hunt the wild boar and prove thy strength.",
                Requirements = new Dictionary<string, int>
                {
                    ["boar.kill"] = 3
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "harvesting tea", Amount = 1 }
                }
            };

            quests[12] = new QuestDefinition
            {
                Id = 12,
                Title = "Quest 12 — Low Grade Fuel",
                Description = "Render fuel from beast and cloth to feed the flame.",
                Requirements = new Dictionary<string, int>
                {
                    ["lowgradefuel"] = 50
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "lowgradefuel", Amount = 100 }
                }
            };

            quests[13] = new QuestDefinition
            {
                Id = 13,
                Title = "Quest 13 — Furnace",
                Description = "Build a furnace and tame fire to shape the earth.",
                Requirements = new Dictionary<string, int>
                {
                    ["furnace"] = 1,
                    ["furnace.placed"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "furnace", Amount = 1 }
                }
            };

            quests[14] = new QuestDefinition
            {
                Id = 14,
                Title = "Quest 14 — Metal Ore",
                Description = "Seek metal in the stone, for stronger works await.",
                Requirements = new Dictionary<string, int>
                {
                    ["metal.ore"] = 500
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "oretea.advanced", Amount = 1 }
                }
            };

            quests[15] = new QuestDefinition
            {
                Id = 15,
                Title = "Quest 15 — Smelting",
                Description = "Smelt raw ore into fragments fit for the forge.",
                Requirements = new Dictionary<string, int>
                {
                    ["metal.fragments"] = 500
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "metal.fragments", Amount = 500 }
                }
            };

            quests[16] = new QuestDefinition
            {
                Id = 16,
                Title = "Quest 16 — Better Door",
                Description = "Replace weak timber with metal, and harden thy hold.",
                Requirements = new Dictionary<string, int>
                {
                    ["door.hinged.metal"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "pie.fish", Amount = 1 }
                }
            };

            quests[17] = new QuestDefinition
            {
                Id = 17,
                Title = "Quest 17 — Repairs",
                Description = "Build a bench to mend gear worn by battle and toil.",
                Requirements = new Dictionary<string, int>
                {
                    ["repair.bench"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "basicblueprintfragment", Amount = 1 }
                }
            };

            quests[18] = new QuestDefinition
            {
                Id = 18,
                Title = "Quest 18 — Road Looting",
                Description = "Walk the old roads and break barrels for forgotten spoils.",
                Requirements = new Dictionary<string, int>
                {
                    ["machete"] = 1,
                    ["road.barrel"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "scrap", Amount = 25 }
                }
            };

            quests[19] = new QuestDefinition
            {
                Id = 19,
                Title = "Quest 19 — Scrap Run",
                Description = "Gather scrap from ruins, for knowledge hides in wreckage.",
                Requirements = new Dictionary<string, int>
                {
                    ["scrap"] = 75
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "scrap", Amount = 25 }
                }
            };

            quests[20] = new QuestDefinition
            {
                Id = 20,
                Title = "Quest 20 — Recycling",
                Description = "Reclaim value from broken things at the recycler.",
                Requirements = new Dictionary<string, int>
                {
                    ["recycler_use"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "basicblueprintfragment", Amount = 1 }
                }
            };

            quests[21] = new QuestDefinition
            {
                Id = 21,
                Title = "Quest 21 — Workbench",
                Description = "Craft a workbench and unlock greater craft.",
                Requirements = new Dictionary<string, int>
                {
                    ["workbench1"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "basicblueprintfragment", Amount = 2 }
                }
            };

            quests[22] = new QuestDefinition
            {
                Id = 22,
                Title = "Quest 22 — Research",
                Description = "Study lost designs and learn forgotten craft.",
                Requirements = new Dictionary<string, int>
                {
                    ["research.table"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "basicblueprintfragment", Amount = 1 }
                }
            };

            quests[23] = new QuestDefinition
            {
                Id = 23,
                Title = "Quest 23 — Engineering",
                Description = "Master advanced craft and bend metal to thy will.",
                Requirements = new Dictionary<string, int>
                {
                    ["iotable"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "metal.fragments", Amount = 500 },
                    new QuestReward { ShortName = "metal.refined", Amount = 50 }
                }
            };

            foreach (var quest in quests.Values)
            {
                quest.HasInventoryTrackedRequirements = quest.Requirements?.Keys.Any(k => InventoryTrackedRequirementKeys.Contains(k)) == true;
            }
        }

        // =========================
        // DATA
        // =========================
        private void LoadPlayerData()
        {
            activeQuests =
                Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, QuestProgress>>(PlayerDataFile)
                ?? new Dictionary<ulong, QuestProgress>();
        }

        private void SavePlayerData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(PlayerDataFile, activeQuests);
        }

        private bool TryGetActiveQuest(BasePlayer player, out QuestProgress progress, out QuestDefinition quest)
        {
            if (player == null)
            {
                progress = null;
                quest = null;
                return false;
            }

            progress = GetProgress(player);

            if (!progress.Started || progress.Completed || progress.RewardPending || !quests.TryGetValue(progress.QuestId, out quest))
            {
                quest = null;
                return false;
            }

            return true;
        }

        private QuestProgress GetProgress(BasePlayer player)
        {
            if (!activeQuests.TryGetValue(player.userID, out var progress))
            {
                progress = new QuestProgress { QuestId = 1 };
                activeQuests[player.userID] = progress;
            }

            if (progress.Completed && quests.ContainsKey(progress.QuestId + 1))
            {
                progress.QuestId++;
                progress.Progress = new Dictionary<string, int>();
                progress.Completed = false;
                progress.RewardPending = false;
                SavePlayerData();
            }

            return progress;
        }

        // =========================
        // GATHER TRACKING
        // =========================
        private void OnDispenserGathered(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            var player = entity.ToPlayer();
            if (player == null) return;

            if (!TryGetActiveQuest(player, out var progress, out var quest))
                return;

            var normalizedKey = NormalizeRequirementKey(item.info.shortname);

            HandleQuestItemAcquired(player, progress, quest, item.info.shortname, item.amount, normalizedKey);

            if (quest.HasInventoryTrackedRequirements && InventoryTrackedRequirementKeys.Contains(normalizedKey))
                UpdateInventoryTrackedProgress(player, progress, quest);
        }

        private void OnItemCraft(ItemCraftTask task, BasePlayer crafter, Item item)
        {
            if (task == null || crafter == null) return;

            craftTaskOwners[task] = crafter.userID;
        }

        private void OnItemCraftFinished(ItemCraftTask task, Item item)
        {
            var player = GetCraftingPlayer(task, item);
            if (player == null) return;

            if (!TryGetActiveQuest(player, out var progress, out var quest))
                return;

            var overrideKey = item.info.shortname == "box.wooden" ? "box.wooden.crafted" : null;
            var normalizedKey = NormalizeRequirementKey(item.info.shortname);

            HandleQuestItemAcquired(player, progress, quest, item.info.shortname, item.amount, overrideKey ?? normalizedKey);

            if (quest.HasInventoryTrackedRequirements && InventoryTrackedRequirementKeys.Contains(normalizedKey))
                UpdateInventoryTrackedProgress(player, progress, quest);

            if (!TaskHasMoreCrafts(task))
                craftTaskOwners.Remove(task);
        }

        private void OnItemCraftCancelled(ItemCraftTask task)
        {
            if (task == null) return;

            craftTaskOwners.Remove(task);
        }

        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            var player = container?.GetOwnerPlayer() ?? container?.entityOwner?.ToPlayer();
            if (player == null) return;

            if (item?.info?.shortname == null) return;

            if (!TryGetActiveQuest(player, out var progress, out var quest))
                return;

            var normalizedKey = NormalizeRequirementKey(item.info.shortname);

            if (quest.HasInventoryTrackedRequirements)
                UpdateInventoryTrackedProgress(player, progress, quest);

            if (ShouldIgnoreContainerGainForGatherQuest(quest, normalizedKey))
                return;

            if (InventoryTrackedRequirementKeys.Contains(normalizedKey))
                return;

            HandleQuestItemAcquired(player, progress, quest, item.info.shortname, item.amount, normalizedKey);
        }

        private void OnEntityBuilt(Planner planner, GameObject go)
        {
            var entity = go.ToBaseEntity();
            var player = planner?.GetOwnerPlayer();
            if (entity == null || player == null) return;

            var shortName = entity.ShortPrefabName ?? string.Empty;

            if (shortName.Equals("cupboard.tool.deployed", StringComparison.OrdinalIgnoreCase))
            {
                HandleQuestItemAcquired(player, "tool.cupboard", 1);
            }
            else if (entity is BuildingBlock)
            {
                HandleQuestItemAcquired(player, "building_block", 1);
            }
            else if (shortName.IndexOf("furnace", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                HandleQuestItemAcquired(player, "furnace.placed", 1);
            }
            else if (shortName.IndexOf("door.hinged", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var normalizedDoor = NormalizeRequirementKey(shortName);

                if (normalizedDoor == "door.hinged.wood" || normalizedDoor == "door.hinged.metal")
                    HandleQuestItemAcquired(player, shortName, 1, normalizedDoor);
            }
            else if (shortName.IndexOf("box.wooden", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                HandleQuestItemAcquired(player, shortName, 1, "box.wooden.placed");
            }
            else if (shortName.IndexOf("lock.key", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                HandleQuestItemAcquired(player, "lock.key", 1);
            }
        }

        private void OnCupboardAuthorize(BuildingPrivlidge privilege, BasePlayer player)
        {
            if (privilege == null || player == null) return;

            HandleQuestItemAcquired(player, "tc_auth", 1);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;

            var player = info.InitiatorPlayer;
            if (player == null) return;

            var shortName = entity.ShortPrefabName ?? string.Empty;

            if (shortName.IndexOf("boar", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                HandleQuestItemAcquired(player, "boar.kill", 1);
            }
            else if (shortName.IndexOf("barrel", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                HandleQuestItemAcquired(player, "road.barrel", 1);
            }
        }

        private void OnRecyclerToggle(Recycler recycler, BasePlayer player)
        {
            if (recycler == null || player == null) return;

            HandleQuestItemAcquired(player, "recycler_use", 1);
        }

        private void HandleQuestItemAcquired(BasePlayer player, QuestProgress progress, QuestDefinition quest, string shortName, int amount, string requirementKeyOverride = null)
        {
            if (string.IsNullOrEmpty(shortName) || amount <= 0 || quest == null)
                return;

            var requirementKey = requirementKeyOverride ?? NormalizeRequirementKey(shortName);

            if (!quest.Requirements.TryGetValue(requirementKey, out var requiredAmount)) return;

            if (!progress.Progress.ContainsKey(requirementKey))
                progress.Progress[requirementKey] = 0;

            progress.Progress[requirementKey] = Mathf.Min(
                progress.Progress[requirementKey] + amount,
                requiredAmount
            );

            EvaluateQuestProgressFulfillment(player, progress, quest);
        }

        private void HandleQuestItemAcquired(BasePlayer player, string shortName, int amount, string requirementKeyOverride = null)
        {
            if (!TryGetActiveQuest(player, out var progress, out var quest))
                return;

            HandleQuestItemAcquired(player, progress, quest, shortName, amount, requirementKeyOverride);
        }

        private void EvaluateQuestProgressFulfillment(BasePlayer player, QuestProgress progress, QuestDefinition quest)
        {
            if (questUiVisible.Contains(player.userID))
                DrawUI(player);

            foreach (var req in quest.Requirements)
            {
                int cur = progress.Progress.TryGetValue(req.Key, out var v) ? v : 0;
                if (cur < req.Value) return;
            }

            TryFinishQuest(player, progress, quest);
        }

        private void UpdateInventoryTrackedProgress(BasePlayer player)
        {
            if (!TryGetActiveQuest(player, out var progress, out var quest))
                return;

            UpdateInventoryTrackedProgress(player, progress, quest);
        }

        private void UpdateInventoryTrackedProgress(BasePlayer player, QuestProgress progress, QuestDefinition quest)
        {
            if (player == null || quest == null || !quest.HasInventoryTrackedRequirements)
                return;

            bool changed = false;

            foreach (var req in quest.Requirements)
            {
                if (!InventoryTrackedRequirementKeys.Contains(req.Key))
                    continue;

                var def = FindItemDefinitionWithFallback(req.Key);
                if (def == null)
                    continue;

                int existing = progress.Progress.TryGetValue(req.Key, out var current) ? current : 0;
                int total = CountItemAcrossPlayerInventories(player, def);
                int clamped = Mathf.Min(req.Value, Math.Max(existing, total));

                if (clamped != existing)
                {
                    progress.Progress[req.Key] = clamped;
                    changed = true;
                }
            }

            if (changed)
                EvaluateQuestProgressFulfillment(player, progress, quest);
        }

        private int CountItemAcrossPlayerInventories(BasePlayer player, ItemDefinition definition)
        {
            if (player == null || definition == null)
                return 0;

            int total = 0;

            void AddFromContainer(ItemContainer container)
            {
                if (container == null) return;

                foreach (var item in container.itemList)
                {
                    if (item?.info == null) continue;
                    if (!item.info.shortname.Equals(definition.shortname, StringComparison.OrdinalIgnoreCase)) continue;

                    total += item.amount;
                }
            }

            AddFromContainer(player.inventory?.containerMain);
            AddFromContainer(player.inventory?.containerBelt);

            return total;
        }

        // =========================
        // REWARD GATING
        // =========================
        private void TryFinishQuest(BasePlayer player, QuestProgress progress, QuestDefinition quest)
        {
            if (rewardDelayPending.Contains(player.userID))
                return;

            if (!HasSpaceForRewards(player, quest))
            {
                progress.RewardPending = true;
                SavePlayerData();

                SendReply(player, Prefix + InvFullMsg);
                return; // 🚫 NO UI, NO REDRAW
            }

            progress.RewardPending = true;
            SavePlayerData();
            rewardDelayPending.Add(player.userID);

            timer.Once(RewardDelaySeconds, () =>
            {
                rewardDelayPending.Remove(player.userID);

                if (player == null || !player.IsConnected)
                    return;

                if (!HasSpaceForRewards(player, quest))
                {
                    SendReply(player, Prefix + InvFullMsg);
                    progress.RewardPending = true;
                    SavePlayerData();
                    return;
                }

                GiveRewards(player, quest);

                bool hasNextQuest = quests.ContainsKey(progress.QuestId + 1);

                if (hasNextQuest)
                {
                    progress.QuestId++;
                    progress.Progress = new Dictionary<string, int>();
                    progress.Completed = false;
                    progress.RewardPending = false;
                    SavePlayerData();

                    SendReply(player, Prefix + $"Next quest unlocked: {quests[progress.QuestId].Title}");
                }
                else
                {
                    progress.Completed = true;
                    progress.RewardPending = false;
                    SavePlayerData();

                    SendReply(player, BuildStarterCompletionMessage());
                }

                CuiHelper.DestroyUi(player, UiRoot);
                questUiVisible.Remove(player.userID);

                PlayQuestCompleteSound(player);

                timer.Once(UiSwapDelay, () =>
                {
                    if (player == null || !player.IsConnected) return;
                    CuiHelper.DestroyUi(player, QuestCompleteRoot);
                    questCompleteVisible.Remove(player.userID);
                    DrawQuestCompleteUI(player, quest);
                    questCompleteVisible.Add(player.userID);
                });
            });
        }

        [ChatCommand("reward")]
        private void CmdReward(BasePlayer player, string cmd, string[] args)
        {
            if (player == null) return;

            var progress = GetProgress(player);
            if (!progress.RewardPending) return;

            TryFinishQuest(player, progress, quests[progress.QuestId]);
        }

        private void GiveRewards(BasePlayer player, QuestDefinition quest)
        {
            foreach (var reward in quest.Rewards)
            {
                var definition = FindItemDefinitionWithFallback(reward.ShortName);
                if (definition == null)
                {
                    PrintWarning($"Quest reward item not found: {reward.ShortName}");
                    continue;
                }

                var item = ItemManager.Create(definition, reward.Amount);
                if (item != null)
                    player.inventory.GiveItem(item);
            }
        }

        private bool HasSpaceForRewards(BasePlayer player, QuestDefinition quest)
        {
            var main = player.inventory.containerMain;
            var belt = player.inventory.containerBelt;

            foreach (var reward in quest.Rewards)
            {
                var definition = FindItemDefinitionWithFallback(reward.ShortName);
                if (definition == null)
                    continue;

                int remaining = reward.Amount;
                remaining = ConsumeContainerSpace(main, definition, remaining);
                remaining = ConsumeContainerSpace(belt, definition, remaining);

                if (remaining > 0)
                    return false;
            }

            return true;
        }

        private int ConsumeContainerSpace(ItemContainer container, ItemDefinition definition, int remaining)
        {
            if (container == null || remaining <= 0)
                return remaining;

            int maxStack = Math.Max(1, definition.stackable);

            foreach (var item in container.itemList)
            {
                if (item?.info == null) continue;
                if (item.info != definition) continue;

                int room = maxStack - item.amount;
                if (room <= 0) continue;

                int used = Math.Min(room, remaining);
                remaining -= used;

                if (remaining <= 0)
                    return 0;
            }

            int emptySlots = container.capacity - container.itemList.Count;
            if (emptySlots > 0)
            {
                int slotCapacity = emptySlots * maxStack;
                remaining = Math.Max(0, remaining - slotCapacity);
            }

            return remaining;
        }

        private void PlayQuestCompleteSound(BasePlayer player)
        {
            Effect.server.Run(
                "assets/prefabs/deployable/research table/effects/research-success.prefab",
                player.transform.position
            );
        }

        // =========================
        // QUEST COMPLETE BUTTON
        // =========================
        [ConsoleCommand("quests.complete.next")]
        private void CmdQuestCompleteNext(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            CuiHelper.DestroyUi(player, QuestCompleteRoot);
            questCompleteVisible.Remove(player.userID);

            var progress = GetProgress(player);

            if (progress.Completed && quests.ContainsKey(progress.QuestId + 1))
            {
                progress.QuestId++;
                progress.Progress = new Dictionary<string, int>();
                progress.Completed = false;
                progress.RewardPending = false;
                SavePlayerData();
            }

            if (quests.ContainsKey(progress.QuestId) && !progress.Completed)
            {
                DrawUI(player);
                questUiVisible.Add(player.userID);
                return;
            }

            SendReply(player,
                Prefix + "Starting quests complete! Do /quest to further your adventures!");
        }

        // =========================
        // COMMANDS
        // =========================
        [ChatCommand("quest")]
        private void CmdQuest(BasePlayer player, string cmd, string[] args)
        {
            if (player == null) return;

            var progress = GetProgress(player);

            if (!progress.Started)
            {
                progress.Started = true;
                SavePlayerData();
            }

            if (args.Length > 0)
            {
                if (!progress.Completed)
                {
                    SendReply(player, Prefix + "Complete the starter quest before starting quest chains.");
                    return;
                }

                SendReply(player, Prefix + DevPlaceholderMsg);
                return;
            }

            if (questUiVisible.Contains(player.userID))
            {
                CuiHelper.DestroyUi(player, UiRoot);
                questUiVisible.Remove(player.userID);
                return;
            }

            if (progress.Completed)
            {
                SendReply(player, BuildStarterCompletionMessage());
                return;
            }

            DrawUI(player);
            questUiVisible.Add(player.userID);
        }

        [ChatCommand("questreset")]
        private void CmdQuestReset(BasePlayer player, string cmd, string[] args)
        {
            if (!IsAdmin(player)) return;

            int targetQuest;
            BasePlayer target;

            if (args.Length == 0)
            {
                targetQuest = 1;
                target = player;
            }
            else
            {
                if (!int.TryParse(args[0], out targetQuest) || !quests.ContainsKey(targetQuest))
                {
                    SendReply(player, Prefix + "Usage: /questreset <questId> [player name|steamId|me]");
                    return;
                }

                target = ResolvePlayerTarget(player, args.Length > 1 ? args[1] : "me");
                if (target == null)
                {
                    SendReply(player, Prefix + "Target player not found.");
                    return;
                }
            }

            activeQuests[target.userID] = new QuestProgress { QuestId = targetQuest, Started = true };
            SavePlayerData();

            if (target.IsConnected)
            {
                CuiHelper.DestroyUi(target, UiRoot);
                CuiHelper.DestroyUi(target, QuestCompleteRoot);

                questUiVisible.Remove(target.userID);
                questCompleteVisible.Remove(target.userID);

                DrawUI(target);
                questUiVisible.Add(target.userID);
            }

            SendReply(player, Prefix + $"Reset quest progress to Quest {targetQuest} for {target.displayName}.");
        }

        private void ForceCompleteQuest(BasePlayer target, int questId)
        {
            if (target == null || !quests.TryGetValue(questId, out var quest))
                return;

            // Clear existing UI immediately so the completion overlay doesn't overlap with any open quest panel
            CuiHelper.DestroyUi(target, UiRoot);
            questUiVisible.Remove(target.userID);
            CuiHelper.DestroyUi(target, QuestCompleteRoot);
            questCompleteVisible.Remove(target.userID);

            var progress = GetProgress(target);
            progress.QuestId = questId;
            progress.Progress = new Dictionary<string, int>();
            progress.Completed = false;
            progress.RewardPending = false;
            progress.Started = true;
            SavePlayerData();

            if (!HasSpaceForRewards(target, quest))
            {
                SendReply(target, Prefix + InvFullMsg);
                return;
            }

            if (rewardDelayPending.Contains(target.userID))
                return;

            rewardDelayPending.Add(target.userID);
            progress.RewardPending = true;
            SavePlayerData();

            timer.Once(RewardDelaySeconds, () =>
            {
                rewardDelayPending.Remove(target.userID);

                if (target == null || !target.IsConnected)
                    return;

                GiveRewards(target, quest);

                progress.Completed = true;
                progress.RewardPending = false;
                SavePlayerData();

                CuiHelper.DestroyUi(target, UiRoot);
                questUiVisible.Remove(target.userID);

                PlayQuestCompleteSound(target);
                CuiHelper.DestroyUi(target, QuestCompleteRoot);
                questCompleteVisible.Remove(target.userID);
                DrawQuestCompleteUI(target, quest);
                questCompleteVisible.Add(target.userID);

                if (quests.ContainsKey(questId + 1))
                {
                    progress.QuestId = questId + 1;
                    progress.Progress = new Dictionary<string, int>();
                    progress.Completed = false;
                    progress.RewardPending = false;
                    SavePlayerData();
                }
            });
        }

        [ChatCommand("questcomplete")]
        private void CmdQuestComplete(BasePlayer player, string cmd, string[] args)
        {
            if (!IsAdmin(player)) return;

            if (args.Length == 0 || !int.TryParse(args[0], out var questId) || !quests.ContainsKey(questId))
            {
                SendReply(player, Prefix + "Usage: /questcomplete <questId> [player name|steamId|me]");
                return;
            }

            var target = ResolvePlayerTarget(player, args.Length > 1 ? args[1] : "me");
            if (target == null)
            {
                SendReply(player, Prefix + "Target player not found.");
                return;
            }

            ForceCompleteQuest(target, questId);
            SendReply(player, Prefix + $"Forced completion of Quest {questId} for {target.displayName}.");
        }

        // =========================
        // UI (QUEST)
        // =========================
        private void DrawUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiRoot);

            var progress = GetProgress(player);
            var quest = quests[progress.QuestId];

            var lines = BuildDescriptionLines(quest);

            float parchmentHeight = Mathf.Max(LineHeight * 2, lines.Count * LineHeight);
            float totalHeight = QuestBarHeight + parchmentHeight + GoalBarHeight;

            var c = new CuiElementContainer();

            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = $"0.38 {HotbarOffsetY}", AnchorMax = $"0.61 {HotbarOffsetY + totalHeight}" }
            }, "Hud", UiRoot);

            float currentTop = 1f;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - QuestBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, UiRoot, QuestBar);

            c.Add(new CuiLabel
            {
                Text = { Text = quest.Title, FontSize = QuestFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, QuestBar);

            currentTop -= QuestBarHeight;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.85 0.78 0.63 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - parchmentHeight}", AnchorMax = $"1 {currentTop}" }
            }, UiRoot, Parchment);

            float textHeight = lines.Count * LineHeight;
            float padding = (parchmentHeight - textHeight) / 2f;
            float startY = 1f - (padding / parchmentHeight);

            for (int i = 0; i < lines.Count; i++)
            {
                float yMax = startY - (i * LineHeight / parchmentHeight);
                float yMin = yMax - (LineHeight / parchmentHeight);

                c.Add(new CuiLabel
                {
                    Text = { Text = lines[i], FontSize = BodyFontSize, Align = TextAnchor.MiddleCenter, Color = "0.23 0.18 0.12 1" },
                    RectTransform = { AnchorMin = $"0 {yMin}", AnchorMax = $"1 {yMax}" }
                }, Parchment);
            }

            currentTop -= parchmentHeight;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - GoalBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, UiRoot, GoalBar);

            c.Add(new CuiLabel
            {
                Text = { Text = BuildGoalText(progress, quest), FontSize = GoalFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, GoalBar);

            CuiHelper.AddUi(player, c);
        }

        // =========================
        // UI (QUEST COMPLETE)
        // =========================
        private void DrawQuestCompleteUI(BasePlayer player, QuestDefinition completedQuest)
        {
            CuiHelper.DestroyUi(player, QuestCompleteRoot);

            float parchmentHeight = 2 * LineHeight;
            float totalHeight = QuestBarHeight + parchmentHeight + GoalBarHeight;

            var c = new CuiElementContainer();

            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = $"0.38 {HotbarOffsetY}", AnchorMax = $"0.61 {HotbarOffsetY + totalHeight}" }
            }, "Hud", QuestCompleteRoot);

            float currentTop = 1f;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - QuestBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, QuestCompleteRoot, QuestCompleteTop);

            c.Add(new CuiLabel
            {
                Text = { Text = "QUEST COMPLETE", FontSize = QuestFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, QuestCompleteTop);

            currentTop -= QuestBarHeight;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.85 0.78 0.63 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - parchmentHeight}", AnchorMax = $"1 {currentTop}" }
            }, QuestCompleteRoot, QuestCompleteParchment);

            c.Add(new CuiLabel
            {
                Text = { Text = "Rewards Received:", FontSize = BodyFontSize, Align = TextAnchor.MiddleCenter, Color = "0.23 0.18 0.12 1" },
                RectTransform = { AnchorMin = "0 0.5", AnchorMax = "1 1" }
            }, QuestCompleteParchment);

            c.Add(new CuiLabel
            {
                Text = { Text = BuildRewardLine(completedQuest), FontSize = BodyFontSize, Align = TextAnchor.MiddleCenter, Color = "0.23 0.18 0.12 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.5" }
            }, QuestCompleteParchment);

            currentTop -= parchmentHeight;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - GoalBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, QuestCompleteRoot, QuestCompleteBottom);

            c.Add(new CuiButton
            {
                Button = { Command = "quests.complete.next", Color = "0.35 0.10 0.10 0.85" },
                Text = { Text = "Next Quest", FontSize = GoalFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" },
                RectTransform = { AnchorMin = "0.40 0.10", AnchorMax = "0.60 0.80" }
            }, QuestCompleteBottom);

            CuiHelper.AddUi(player, c);
        }

        // =========================
        // HELPERS
        // =========================
        private bool ShouldIgnoreContainerGainForGatherQuest(QuestDefinition quest, string normalizedKey)
        {
            if (quest == null || string.IsNullOrEmpty(normalizedKey))
                return false;

            if ((quest.Id == 1 || quest.Id == 3) &&
                (normalizedKey.Equals("wood", StringComparison.OrdinalIgnoreCase) ||
                 normalizedKey.Equals("stones", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        private string BuildStarterCompletionMessage()
        {
            return "<size=18><color=#D87C2A><b>Starter quest complete!</b></color></size>\n" +
                   "<color=#FFFFFF>The following quest chains have been unlocked: Fisherman, Hunter, Diver, Lumberjack, Treasure Hunter, Bounty Hunter, Explorer, Barrel Smasher. Start one with /quest <b>NAME</b>.</color>\n" +
                   "<color=#AAAAAA>The questing system is still in a very early stage and many quest chains may not be available yet! Leave me feedback in Discord #bug-reports.</color>";
        }

        private List<string> BuildDescriptionLines(QuestDefinition quest)
        {
            var description = string.IsNullOrWhiteSpace(quest.Description)
                ? MissingDescriptionFallback
                : quest.Description.Trim();

            var lines = WrapText(description, MaxCharsPerLine);
            if (lines.Count == 0)
            {
                lines.Add(MissingDescriptionFallback);
            }

            if (lines.Count > MaxLines)
                lines = lines.GetRange(0, MaxLines);

            return lines;
        }

        private string BuildGoalText(QuestProgress progress, QuestDefinition quest)
        {
            var sb = new StringBuilder("Goal: ");
            foreach (var req in quest.Requirements)
            {
                int cur = progress.Progress.TryGetValue(req.Key, out var v) ? v : 0;
                sb.Append($"{GetRequirementDisplayName(req.Key)} {cur}/{req.Value}  ");
            }
            return sb.ToString();
        }

        private string NormalizeRequirementKey(string shortName)
        {
            if (string.IsNullOrEmpty(shortName))
                return shortName;

            if (shortName.EndsWith(".deployed", StringComparison.OrdinalIgnoreCase))
                shortName = shortName.Substring(0, shortName.Length - ".deployed".Length);

            if (shortName.IndexOf("legacy", StringComparison.OrdinalIgnoreCase) >= 0
                && shortName.IndexOf("bow", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "bow.hunting";
            }

            if (shortName.StartsWith("door.hinged", StringComparison.OrdinalIgnoreCase) || shortName.StartsWith("door.double.hinged", StringComparison.OrdinalIgnoreCase))
            {
                if (shortName.IndexOf("wood", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "door.hinged.wood";

                if (shortName.IndexOf("metal", StringComparison.OrdinalIgnoreCase) >= 0 || shortName.IndexOf("toptier", StringComparison.OrdinalIgnoreCase) >= 0 || shortName.IndexOf("armored", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "door.hinged.metal";
            }

            if (shortName.Equals("bow", StringComparison.OrdinalIgnoreCase) || shortName.Equals("weapon.bow", StringComparison.OrdinalIgnoreCase)
                || shortName.IndexOf("bow.hunting", StringComparison.OrdinalIgnoreCase) >= 0 || shortName.IndexOf("bow.compound", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "bow.hunting";
            }

            if (shortName.IndexOf("repair", StringComparison.OrdinalIgnoreCase) >= 0
                && shortName.IndexOf("bench", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "repair.bench";
            }

            switch (shortName)
            {
                case "workbench3":
                    return "iotable";
                default:
                    return shortName;
            }
        }

        private string BuildRewardLine(QuestDefinition quest)
        {
            var sb = new StringBuilder();
            int shown = 0;

            foreach (var r in quest.Rewards)
            {
                if (shown >= 2) break;
                if (shown > 0) sb.Append("   ");
                sb.Append($"x{r.Amount} {GetItemDisplayName(r.ShortName)}");
                shown++;
            }

            return sb.ToString();
        }

        private BasePlayer ResolvePlayerTarget(BasePlayer caller, string targetArg)
        {
            if (string.IsNullOrEmpty(targetArg) || targetArg.Equals("me", StringComparison.OrdinalIgnoreCase))
                return caller;

            if (ulong.TryParse(targetArg, out var userId))
                return BasePlayer.FindAwakeOrSleeping(userId.ToString());

            foreach (var active in BasePlayer.activePlayerList)
            {
                if (active.displayName.IndexOf(targetArg, StringComparison.OrdinalIgnoreCase) >= 0)
                    return active;
            }

            foreach (var sleeper in BasePlayer.sleepingPlayerList)
            {
                if (sleeper.displayName.IndexOf(targetArg, StringComparison.OrdinalIgnoreCase) >= 0)
                    return sleeper;
            }

            return null;
        }

        private List<string> WrapText(string text, int maxChars)
        {
            var words = text.Split(' ');
            var lines = new List<string>();
            var current = new StringBuilder();

            foreach (var word in words)
            {
                if (current.Length + word.Length + 1 > maxChars)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                    current.Append(" ");

                current.Append(word);
            }

            if (current.Length > 0)
                lines.Add(current.ToString());

            return lines;
        }

        private BasePlayer GetCraftingPlayer(ItemCraftTask task, Item item)
        {
            if (task != null && craftTaskOwners.TryGetValue(task, out var ownerId))
            {
                var ownerPlayer = BasePlayer.FindByID(ownerId) ?? BasePlayer.FindSleeping(ownerId);
                if (ownerPlayer != null) return ownerPlayer;
            }

            var player = item?.GetOwnerPlayer();
            if (player != null) return player;

            if (task == null) return null;

            var taskType = task.GetType();

            var taskOwnerProp = taskType.GetProperty("taskOwner");
            if (taskOwnerProp != null)
            {
                player = taskOwnerProp.GetValue(task) as BasePlayer;
                if (player != null) return player;
            }

            var ownerProp = taskType.GetProperty("owner");
            if (ownerProp != null)
            {
                player = ownerProp.GetValue(task) as BasePlayer;
                if (player != null) return player;

                var ownerValue = ownerProp.GetValue(task);
                var nestedOwner = ownerValue?.GetType().GetProperty("owner")?.GetValue(ownerValue) as BasePlayer;
                if (nestedOwner != null) return nestedOwner;

                player = ExtractPlayerFromOwner(ownerValue);
                if (player != null) return player;

                player = FindPlayerFromId(ownerValue);
                if (player != null) return player;
            }

            var taskOwnerField = taskType.GetField("taskOwner");
            if (taskOwnerField != null)
            {
                player = taskOwnerField.GetValue(task) as BasePlayer;
                if (player != null) return player;
            }

            var ownerField = taskType.GetField("owner");
            if (ownerField != null)
            {
                player = ownerField.GetValue(task) as BasePlayer;
                if (player != null) return player;

                var ownerValue = ownerField.GetValue(task);
                var nestedOwner = ownerValue?.GetType().GetProperty("owner")?.GetValue(ownerValue) as BasePlayer;
                if (nestedOwner != null) return nestedOwner;

                player = ExtractPlayerFromOwner(ownerValue);
                if (player != null) return player;

                player = FindPlayerFromId(ownerValue);
                if (player != null) return player;
            }

            player = FindPlayerFromTask(task);
            if (player != null) return player;

            return null;
        }

        private bool TaskHasMoreCrafts(ItemCraftTask task)
        {
            if (task == null)
                return false;

            var taskType = task.GetType();

            var amountField = taskType.GetField("amount");
            if (amountField?.GetValue(task) is int fieldAmount && fieldAmount > 1)
                return true;

            var amountProp = taskType.GetProperty("amount");
            if (amountProp?.GetValue(task) is int propAmount && propAmount > 1)
                return true;

            return false;
        }

        private BasePlayer ExtractPlayerFromOwner(object ownerValue)
        {
            if (ownerValue == null) return null;

            var ownerType = ownerValue.GetType();

            var taskOwnerProp = ownerType.GetProperty("taskOwner");
            if (taskOwnerProp != null)
            {
                var player = taskOwnerProp.GetValue(ownerValue) as BasePlayer;
                if (player != null) return player;
            }

            var taskOwnerField = ownerType.GetField("taskOwner");
            if (taskOwnerField != null)
            {
                var player = taskOwnerField.GetValue(ownerValue) as BasePlayer;
                if (player != null) return player;
            }

            var ownerPlayerProp = ownerType.GetProperty("ownerPlayer");
            if (ownerPlayerProp != null)
            {
                var player = ownerPlayerProp.GetValue(ownerValue) as BasePlayer;
                if (player != null) return player;
            }

            var ownerPlayerField = ownerType.GetField("ownerPlayer");
            if (ownerPlayerField != null)
            {
                var player = ownerPlayerField.GetValue(ownerValue) as BasePlayer;
                if (player != null) return player;
            }

            var getOwnerPlayerMethod = ownerType.GetMethod("GetOwnerPlayer");
            if (getOwnerPlayerMethod != null)
            {
                var candidate = getOwnerPlayerMethod.Invoke(ownerValue, null) as BasePlayer;
                if (candidate != null) return candidate;
            }

            var userIdProp = ownerType.GetProperty("userID") ?? ownerType.GetProperty("userid") ?? ownerType.GetProperty("UserID");
            if (userIdProp != null)
            {
                var player = FindPlayerFromId(userIdProp.GetValue(ownerValue));
                if (player != null) return player;
            }

            var userIdField = ownerType.GetField("userID") ?? ownerType.GetField("userid") ?? ownerType.GetField("UserID");
            if (userIdField != null)
            {
                var player = FindPlayerFromId(userIdField.GetValue(ownerValue));
                if (player != null) return player;
            }

            return null;
        }

        private BasePlayer FindPlayerFromTask(ItemCraftTask task)
        {
            var taskType = task.GetType();

            var ownerIdProp = taskType.GetProperty("ownerID") ?? taskType.GetProperty("OwnerID") ?? taskType.GetProperty("ownerId");
            if (ownerIdProp != null)
            {
                var player = FindPlayerFromId(ownerIdProp.GetValue(task));
                if (player != null) return player;
            }

            var ownerIdField = taskType.GetField("ownerID") ?? taskType.GetField("OwnerID") ?? taskType.GetField("ownerId");
            if (ownerIdField != null)
            {
                var player = FindPlayerFromId(ownerIdField.GetValue(task));
                if (player != null) return player;
            }

            return null;
        }

        private BasePlayer FindPlayerFromId(object idValue)
        {
            if (idValue == null) return null;

            ulong id;
            if (idValue is ulong ulongId)
            {
                id = ulongId;
            }
            else if (idValue is long longId)
            {
                id = (ulong)longId;
            }
            else if (idValue is string idString && ulong.TryParse(idString, out var parsed))
            {
                id = parsed;
            }
            else
            {
                return null;
            }

            var player = BasePlayer.FindByID(id) ?? BasePlayer.FindSleeping(id);
            return player;
        }

        private string GetItemDisplayName(string shortName)
        {
            var def = FindItemDefinitionWithFallback(shortName);
            return def != null ? def.displayName.english : shortName;
        }

        private string GetRequirementDisplayName(string key)
        {
            switch (key)
            {
                case "boar.kill":
                    return "Slay Boars";
                case "tc_auth":
                    return "Authorization";
                case "building_block":
                    return "Building pieces";
                case "road.barrel":
                    return "Road barrels";
                case "recycler_use":
                    return "Recycler";
                case "furnace.placed":
                    return "Furnace placed";
                case "box.wooden.crafted":
                    return "Boxes crafted";
                case "box.wooden.placed":
                    return "Boxes placed";
                case "door.hinged.wood":
                    return "Wood door";
                case "door.hinged.metal":
                    return "Metal door";
                case "iotable":
                    return "Engineering Workbench";
                case "repair.bench":
                    return "Repair Bench";
                default:
                    return GetItemDisplayName(key);
            }
        }

        private ItemDefinition FindItemDefinitionWithFallback(string key)
        {
            var def = ItemManager.FindItemDefinition(key);
            if (def != null)
                return def;

            if (RewardShortNameAliases.TryGetValue(key, out var mapped))
            {
                def = ItemManager.FindItemDefinition(mapped);
                if (def != null)
                    return def;
            }

            foreach (var candidate in ItemManager.itemList)
            {
                if (candidate == null) continue;

                if (!string.IsNullOrEmpty(candidate.shortname) && candidate.shortname.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            foreach (var candidate in ItemManager.itemList)
            {
                if (candidate == null || candidate.displayName == null) continue;

                var name = candidate.displayName.english;
                if (string.IsNullOrEmpty(name)) continue;

                if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return candidate;

                if (name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return candidate;
            }

            return null;
        }
    }
}
