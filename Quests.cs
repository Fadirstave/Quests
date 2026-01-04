using System;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
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
            public bool Completed;
            public bool RewardPending;
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
        private Dictionary<ulong, QuestProgress> dukeQuests;
        private readonly Dictionary<ItemCraftTask, ulong> craftTaskOwners = new Dictionary<ItemCraftTask, ulong>();
        private readonly HashSet<ulong> rewardDelayPending = new HashSet<ulong>();

        private const string PlayerDataFile = "Quests_PlayerData";
        private const string DukeDataFile = "Quests_DukeData";

        // =========================
        // UI IDS
        // =========================
        private const string UiRoot = "QuestsUI.Root";
        private const string QuestBar = "QuestsUI.QuestBar";
        private const string Parchment = "QuestsUI.Parchment";
        private const string GoalBar = "QuestsUI.GoalBar";

        private const string QuestCompleteRoot = "QuestsUI.CompleteOverlay";
        private const string DukeCompleteRoot = "QuestsUI.DukeCompleteOverlay";
        private const string QuestCompleteTop = "QuestsUI.CompleteTop";
        private const string QuestCompleteParchment = "QuestsUI.CompleteParchment";
        private const string QuestCompleteBottom = "QuestsUI.CompleteBottom";

        private readonly HashSet<ulong> questUiVisible = new HashSet<ulong>();
        private readonly HashSet<ulong> questCompleteVisible = new HashSet<ulong>();
        private readonly HashSet<ulong> dukeCompleteVisible = new HashSet<ulong>();
        private readonly HashSet<ulong> dukeUiVisible = new HashSet<ulong>();
        private readonly HashSet<ulong> pendingUiRestore = new HashSet<ulong>();

        // =========================
        // UI CONSTANTS
        // =========================
        private const int MaxCharsPerLine = 85;
        private const int MaxLines = 2;

        private const float QuestBarHeight = 0.12f;
        private const float GoalBarHeight = 0.10f;
        private const float LineHeight = 0.055f;
        private const float ParchmentHeight = LineHeight * MaxLines;
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
            "These quests are still in early development and will be added soon!";
        private const string MissingDescriptionFallback = "Description coming soon.";
        private const string DukeChainName = "theduke";
        private const ulong DukeNpcId = 8944230400;
        private const string DukeNpcName = "Duke of the Commonwealth";
        private const int DukeQuestFirstId = 1;
        private const int DukeQuestItemCount = 10;
        private const int DukeQuestFinalId = DukeQuestItemCount + 1;
        private const int DukeQuestIdOffset = 9000;
        private const string DukePriceCommand = "swear fealty";
        private const string EsquirePermission = "guishop.use";
        private const string EsquireTitlePermission = "titlemanager.esquire";

        private static readonly string[] DukeRequiredItems =
        {
            "roadsign.jacket",
            "roadsign.kilt",
            "roadsign.gloves",
            "coffeecan.helmet",
            "hoodie",
            "pants",
            "shoes.boots",
            "salvaged.sword",
            "crossbow",
            "wooden.shield"
        };

        // =========================
        // LIFECYCLE
        // =========================
        private void OnServerInitialized()
        {
            LoadQuests();
            LoadPlayerData();
            LoadDukeData();
            LoadIgnoreData();
        }

        private void Init()
        {
            if (!permission.PermissionExists(EsquirePermission))
            {
                permission.RegisterPermission(EsquirePermission, this);
            }

            if (!permission.PermissionExists(EsquireTitlePermission))
            {
                permission.RegisterPermission(EsquireTitlePermission, this);
            }
        }

        private void Unload()
        {
            SavePlayerData();
            SaveDukeData();
            SaveIgnoreData();

            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, UiRoot);
                CuiHelper.DestroyUi(player, QuestCompleteRoot);
                CuiHelper.DestroyUi(player, DukeCompleteRoot);
            }
        }

        private void OnServerSave()
        {
            SaveIgnoreData();
            SaveDukeData();
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

            EnsureEsquirePermissions(player);

            if (!ignoredPlayers.ContainsKey(player.userID))
            {
                // Once per connection
                if (!loginShownThisSession.Contains(player.userID))
                {
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
            }

            timer.Once(1f, () => RestoreQuestUi(player));
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            // Allow showing again next time they connect (unless ignored)
            loginShownThisSession.Remove(player.userID);

            if (questUiVisible.Contains(player.userID) || dukeUiVisible.Contains(player.userID))
            {
                pendingUiRestore.Add(player.userID);
            }

            questUiVisible.Remove(player.userID);
            questCompleteVisible.Remove(player.userID);
            dukeUiVisible.Remove(player.userID);
            dukeCompleteVisible.Remove(player.userID);
            rewardDelayPending.Remove(player.userID);
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

        private void RestoreQuestUi(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
            {
                return;
            }

            if (!pendingUiRestore.Remove(player.userID))
            {
                return;
            }

            if (TryGetActiveDukeQuest(player, out var dukeProgress, out var dukeQuest))
            {
                DrawDukeUI(player, dukeProgress, dukeQuest);
                dukeUiVisible.Add(player.userID);
                return;
            }

            var progress = GetProgress(player);
            if (progress.Started && !progress.Completed && !progress.RewardPending && quests.ContainsKey(progress.QuestId))
            {
                DrawUI(player);
                questUiVisible.Add(player.userID);
            }
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
                    new QuestReward { ShortName = "lantern", Amount = 1 },
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
                Description = "Render animal fat with cloth to produce low grade fuel.",
                Requirements = new Dictionary<string, int>
                {
                    ["lowgradefuel"] = 25
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "lowgradefuel", Amount = 50 }
                }
            };

            quests[13] = new QuestDefinition
            {
                Id = 13,
                Title = "Quest 13 — Stone Furnace",
                Description = "Craft and place a furnace to smelt thy ore.",
                Requirements = new Dictionary<string, int>
                {
                    ["furnace.placed"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "oretea", Amount = 1 }
                }
            };

            quests[14] = new QuestDefinition
            {
                Id = 14,
                Title = "Quest 14 — Smelt Ore",
                Description = "Smelt metal ore to produce metal fragments.",
                Requirements = new Dictionary<string, int>
                {
                    ["metal.fragments"] = 50
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "metal.fragments", Amount = 100 }
                }
            };

            quests[15] = new QuestDefinition
            {
                Id = 15,
                Title = "Quest 15 — Better Tools",
                Description = "Upgrade to metal tools for more efficient work.",
                Requirements = new Dictionary<string, int>
                {
                    ["pickaxe"] = 1,
                    ["hatchet"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "woodtea", Amount = 1 },
                    new QuestReward { ShortName = "oretea", Amount = 1 }
                }
            };

            quests[16] = new QuestDefinition
            {
                Id = 16,
                Title = "Quest 16 — Recycler",
                Description = "Use a recycler to reclaim useful scraps.",
                Requirements = new Dictionary<string, int>
                {
                    ["recycler_use"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "scrap", Amount = 50 }
                }
            };

            quests[17] = new QuestDefinition
            {
                Id = 17,
                Title = "Quest 17 — Metal Door",
                Description = "Craft a sheet metal door to better protect thy home.",
                Requirements = new Dictionary<string, int>
                {
                    ["door.hinged.metal"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "metal.fragments", Amount = 200 }
                }
            };

            quests[18] = new QuestDefinition
            {
                Id = 18,
                Title = "Quest 18 — Recycler",
                Description = "Use a recycler to reclaim useful scraps.",
                Requirements = new Dictionary<string, int>
                {
                    ["recycler_use"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "scrap", Amount = 50 }
                }
            };

            foreach (var quest in quests.Values)
            {
                quest.HasInventoryTrackedRequirements = quest.Requirements.Keys
                    .Any(key => InventoryTrackedRequirementKeys.Contains(NormalizeRequirementKey(key)));
            }
        }

        // =========================
        // DUKE QUESTS
        // =========================
        private QuestDefinition BuildDukeQuest(int questId)
        {
            if (questId == DukeQuestFinalId)
            {
                return new QuestDefinition
                {
                    Id = DukeQuestFinalId,
                    Title = "The Duke of the Commonwealth",
                    Description = "If you are ready, swear fealty before the duke!\nType /quest Swear Fealty",
                    Requirements = new Dictionary<string, int>
                    {
                        ["duke_meet"] = 1
                    },
                    Rewards = new List<QuestReward>()
                };
            }

            if (questId < DukeQuestFirstId || questId > DukeQuestItemCount)
            {
                questId = DukeQuestFirstId;
            }

            var itemIndex = questId - DukeQuestFirstId;
            var requirementItem = DukeRequiredItems[itemIndex];
            var displayName = GetItemDisplayName(requirementItem);

            return new QuestDefinition
            {
                Id = questId,
                Title = $"The Duke's Proof — {displayName}",
                Description = $"Craft and carry a {displayName} to prove thy readiness.",
                Requirements = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [requirementItem] = 1
                },
                Rewards = new List<QuestReward>()
            };
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

        private void LoadDukeData()
        {
            dukeQuests =
                Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, QuestProgress>>(DukeDataFile)
                ?? new Dictionary<ulong, QuestProgress>();
        }

        private void SavePlayerData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(PlayerDataFile, activeQuests);
        }

        private void SaveDukeData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(DukeDataFile, dukeQuests);
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

        private bool TryGetActiveDukeQuest(BasePlayer player, out QuestProgress progress, out QuestDefinition quest)
        {
            progress = null;
            quest = null;

            if (player == null)
            {
                return false;
            }

            progress = GetDukeProgress(player);

            if (!progress.Started || progress.Completed || progress.RewardPending)
            {
                return false;
            }

            quest = BuildDukeQuest(progress.QuestId);
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

        private QuestProgress GetDukeProgress(BasePlayer player)
        {
            if (dukeQuests == null)
            {
                dukeQuests = new Dictionary<ulong, QuestProgress>();
            }

            if (!dukeQuests.TryGetValue(player.userID, out var progress))
            {
                progress = new QuestProgress { QuestId = 1 };
                dukeQuests[player.userID] = progress;
            }

            if (progress.Completed && progress.QuestId < DukeQuestFinalId)
            {
                progress.QuestId++;
                progress.Progress = new Dictionary<string, int>();
                progress.Completed = false;
                progress.RewardPending = false;
                SaveDukeData();
            }

            return progress;
        }

        private bool IsDukeQuestId(int questId) =>
            questId >= DukeQuestIdOffset + DukeQuestFirstId && questId <= DukeQuestIdOffset + DukeQuestFinalId;

        private int ToDukeQuestInternalId(int questId) => questId - DukeQuestIdOffset;

        private void RecordCraftTaskOwner(ItemCraftTask task, BasePlayer player)
        {
            if (task == null || player == null)
                return;

            craftTaskOwners[task] = player.userID;
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null || input.WasJustPressed(BUTTON.USE))
                return;

            if (TryGetActiveQuest(player, out var progress, out var quest))
            {
                HandleQuestUseInput(player, progress, quest);
            }

            if (TryGetActiveDukeQuest(player, out var dukeProgress, out var dukeQuest))
            {
                HandleDukeQuestUseInput(player, dukeProgress, dukeQuest);
            }
        }

        private void HandleQuestUseInput(BasePlayer player, QuestProgress progress, QuestDefinition quest)
        {
            if (quest == null || progress == null)
                return;

            switch (quest.Id)
            {
                case 5:
                    if (progress.Progress.TryGetValue("tc_auth", out var tcAuth) && tcAuth < quest.Requirements["tc_auth"])
                    {
                        progress.Progress["tc_auth"] = quest.Requirements["tc_auth"];
                        SavePlayerData();
                    }
                    break;
            }
        }

        private void HandleDukeQuestUseInput(BasePlayer player, QuestProgress progress, QuestDefinition quest)
        {
            if (quest == null || progress == null)
                return;
        }

        private void OnItemCraftFinished(ItemCraftTask task, Item item)
        {
            if (task == null || item == null)
                return;

            var player = GetCraftingPlayer(task, item);
            if (player == null)
                return;

            HandleCraftedItemAcquired(player, item.info.shortname, item.amount);

            var overrideKey = task?.blueprint?.targetItem?.shortname;
            var normalizedKey = NormalizeRequirementKey(item.info.shortname);
            HandleDukeCraftedItemAcquired(player, item.info.shortname, item.amount, overrideKey ?? normalizedKey);

            if (!TaskHasMoreCrafts(task))
            {
                craftTaskOwners.Remove(task);
            }
        }

        private void OnItemCraftCancelled(ItemCraftTask task)
        {
            if (task == null)
                return;

            craftTaskOwners.Remove(task);
        }

        private void OnItemCraftStarted(ItemCraftTask task, Item item)
        {
            var player = GetCraftingPlayer(task, item);
            if (player == null)
                return;

            RecordCraftTaskOwner(task, player);
        }

        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            if (container == null || item == null)
                return;

            var player = container?.playerOwner;
            if (player == null)
                return;

            var shortName = NormalizeRequirementKey(item.info.shortname);

            if (TryGetActiveQuest(player, out var progress, out var quest))
            {
                if (!ShouldIgnoreContainerGainForGatherQuest(quest, shortName))
                {
                    IncrementRequirement(player, progress, quest, shortName, item.amount);
                    DrawUI(player);
                }
            }

            if (TryGetActiveDukeQuest(player, out var dukeProgress, out var dukeQuest))
            {
                IncrementDukeRequirement(player, dukeProgress, dukeQuest, shortName, item.amount);
            }
        }

        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            if (plan == null || go == null)
                return;

            var player = plan.GetOwnerPlayer();
            if (player == null)
                return;

            if (!TryGetActiveQuest(player, out var progress, out var quest))
                return;

            var entity = go.ToBaseEntity();
            if (entity == null)
                return;

            var prefabName = entity.ShortPrefabName;

            if (prefabName.Contains("cupboard.tool.deployed"))
                IncrementRequirement(player, progress, quest, "tc_auth", 1);

            if (prefabName.Contains("wall") || prefabName.Contains("foundation") || prefabName.Contains("floor") || prefabName.Contains("stairs") || prefabName.Contains("roof"))
                IncrementRequirement(player, progress, quest, "building_block", 1);
        }

        private void OnEntityKill(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info?.InitiatorPlayer == null)
                return;

            var player = info.InitiatorPlayer;

            if (TryGetActiveQuest(player, out var progress, out var quest))
            {
                if (entity.ShortPrefabName.Contains("boar"))
                    IncrementRequirement(player, progress, quest, "boar.kill", 1);
            }
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info?.InitiatorPlayer == null)
                return;

            var player = info.InitiatorPlayer;

            if (TryGetActiveQuest(player, out var progress, out var quest))
            {
                if (entity.ShortPrefabName.Contains("boar"))
                    IncrementRequirement(player, progress, quest, "boar.kill", 1);
            }
        }

        private void HandleCraftedItemAcquired(BasePlayer player, string shortName, int amount)
        {
            if (player == null)
                return;

            if (!TryGetActiveQuest(player, out var progress, out var quest))
                return;

            IncrementRequirement(player, progress, quest, NormalizeRequirementKey(shortName), amount);
            DrawUI(player);
        }

        private void HandleDukeCraftedItemAcquired(BasePlayer player, string shortName, int amount, string requirementKeyOverride = null)
        {
            if (player == null)
                return;

            if (!TryGetActiveDukeQuest(player, out var progress, out var quest))
                return;

            var normalizedKey = NormalizeRequirementKey(shortName);
            var requirementKey = requirementKeyOverride ?? normalizedKey;

            IncrementDukeRequirement(player, progress, quest, requirementKey, amount);
        }

        private void IncrementRequirement(BasePlayer player, QuestProgress progress, QuestDefinition quest, string key, int amount)
        {
            if (quest == null || progress == null)
                return;

            if (!quest.Requirements.ContainsKey(key))
                return;

            if (!progress.Progress.ContainsKey(key))
                progress.Progress[key] = 0;

            progress.Progress[key] += amount;

            if (progress.Progress[key] >= quest.Requirements[key])
                progress.Progress[key] = quest.Requirements[key];

            SavePlayerData();
            TryCompleteQuest(player, progress, quest);
        }

        private void IncrementDukeRequirement(BasePlayer player, QuestProgress progress, QuestDefinition quest, string key, int amount)
        {
            if (quest == null || progress == null)
                return;

            if (!quest.Requirements.ContainsKey(key))
                return;

            if (!progress.Progress.ContainsKey(key))
                progress.Progress[key] = 0;

            progress.Progress[key] += amount;

            if (progress.Progress[key] >= quest.Requirements[key])
                progress.Progress[key] = quest.Requirements[key];

            SaveDukeData();
            TryFinishDukeQuest(player, progress, quest);
        }

        private void TryCompleteQuest(BasePlayer player, QuestProgress progress, QuestDefinition quest)
        {
            if (progress.Completed || progress.RewardPending)
                return;

            foreach (var req in quest.Requirements)
            {
                if (!progress.Progress.TryGetValue(req.Key, out var value) || value < req.Value)
                    return;
            }

            if (!HasSpaceForRewards(player, quest))
            {
                progress.RewardPending = true;
                SavePlayerData();
                SendReply(player, Prefix + InvFullMsg);
                return;
            }

            progress.Completed = true;
            SavePlayerData();

            GiveRewards(player, quest);

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
        }

        private void TryFinishDukeQuest(BasePlayer player, QuestProgress progress, QuestDefinition quest)
        {
            if (progress.Completed || progress.RewardPending)
                return;

            progress.RewardPending = true;
            SaveDukeData();

            if (quest.Id < DukeQuestFinalId)
            {
                progress.QuestId++;
                progress.Progress = new Dictionary<string, int>();
                progress.Completed = false;
                progress.RewardPending = false;
                SaveDukeData();

                SendReply(player, Prefix + $"Next quest unlocked: {BuildDukeQuest(progress.QuestId).Title}");
            }
            else
            {
                GrantEsquireRewards(player);

                progress.Completed = true;
                progress.RewardPending = false;
                SaveDukeData();

                SendReply(player, Prefix + "The Duke recognizes thy service. Thou art now an Esquire.");
            }

            CuiHelper.DestroyUi(player, UiRoot);
            dukeUiVisible.Remove(player.userID);

            PlayQuestCompleteSound(player);
            ShowDukeQuestCompleteUI(player, quest);
            dukeCompleteVisible.Add(player.userID);

            timer.Once(UiSwapDelay, () =>
            {
                if (player == null || !player.IsConnected) return;

                CuiHelper.DestroyUi(player, DukeCompleteRoot);
                dukeCompleteVisible.Remove(player.userID);

                if (!progress.Completed)
                {
                    var nextQuest = BuildDukeQuest(progress.QuestId);
                    DrawDukeUI(player, progress, nextQuest);
                    dukeUiVisible.Add(player.userID);
                }
            });
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

        private void GrantEsquireRewards(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            permission.GrantUserPermission(player.UserIDString, EsquirePermission, this);
            permission.GrantUserPermission(player.UserIDString, EsquireTitlePermission, this);
        }

        private void EnsureEsquirePermissions(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            var progress = GetDukeProgress(player);
            if (!progress.Completed)
            {
                return;
            }

            if (!permission.UserHasPermission(player.UserIDString, EsquirePermission))
            {
                permission.GrantUserPermission(player.UserIDString, EsquirePermission, this);
            }

            if (!permission.UserHasPermission(player.UserIDString, EsquireTitlePermission))
            {
                permission.GrantUserPermission(player.UserIDString, EsquireTitlePermission, this);
            }
        }

        private bool HasSpaceForRewards(BasePlayer player, QuestDefinition quest)
        {
            if (player == null || player.inventory == null)
                return false;

            var itemCount = 0;

            foreach (var reward in quest.Rewards)
            {
                if (ItemManager.FindItemDefinition(reward.ShortName) != null)
                {
                    itemCount++;
                }
            }

            return player.inventory.containerMain.capacity - player.inventory.containerMain.itemList.Count >= itemCount;
        }

        // =========================
        // UI DRAWING
        // =========================
        private void DrawUI(BasePlayer player)
        {
            if (player == null) return;

            CuiHelper.DestroyUi(player, UiRoot);
            questUiVisible.Remove(player.userID);

            if (!TryGetActiveQuest(player, out var progress, out var quest))
                return;

            var questTitle = quest.Title;
            var questLines = BuildDescriptionLines(quest);
            var rewardLine = BuildRewardLine(quest);

            var ui = new CuiElementContainer();

            float currentTop = 1f;

            ui.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - QuestBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, "Hud", UiRoot);

            ui.Add(new CuiLabel
            {
                Text = { Text = questTitle, FontSize = QuestFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, UiRoot, QuestBar);

            currentTop -= QuestBarHeight;

            ui.Add(new CuiPanel
            {
                Image = { Color = $"0.85 0.78 0.63 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - ParchmentHeight}", AnchorMax = $"1 {currentTop}" }
            }, UiRoot, Parchment);

            var lines = questLines;
            float lineHeight = LineHeight;
            float offset = 0f;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                ui.Add(new CuiLabel
                {
                    Text = { Text = line, FontSize = BodyFontSize, Align = TextAnchor.MiddleCenter, Color = "0.23 0.18 0.12 1" },
                    RectTransform = { AnchorMin = $"0 {offset}", AnchorMax = $"1 {offset + lineHeight}" }
                }, Parchment);
                offset += lineHeight;
            }

            currentTop -= ParchmentHeight;

            ui.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - GoalBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, UiRoot, GoalBar);

            ui.Add(new CuiLabel
            {
                Text = { Text = BuildGoalText(progress, quest), FontSize = GoalFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, GoalBar);

            currentTop -= GoalBarHeight;

            ui.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = $"0 {currentTop - GoalBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, UiRoot, $"{GoalBar}.Spacer");

            ui.Add(new CuiLabel
            {
                Text = { Text = rewardLine, FontSize = BodyFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, $"{GoalBar}.Spacer");

            CuiHelper.AddUi(player, ui);

            questUiVisible.Add(player.userID);
        }

        private void DrawDukeUI(BasePlayer player, QuestProgress progress, QuestDefinition quest)
        {
            if (player == null || quest == null)
                return;

            CuiHelper.DestroyUi(player, UiRoot);
            dukeUiVisible.Remove(player.userID);

            var questTitle = quest.Title;
            var questLines = BuildDescriptionLines(quest);

            var ui = new CuiElementContainer();

            float currentTop = 1f;

            ui.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - QuestBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, "Hud", UiRoot);

            ui.Add(new CuiLabel
            {
                Text = { Text = questTitle, FontSize = QuestFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, UiRoot, QuestBar);

            currentTop -= QuestBarHeight;

            ui.Add(new CuiPanel
            {
                Image = { Color = $"0.85 0.78 0.63 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - ParchmentHeight}", AnchorMax = $"1 {currentTop}" }
            }, UiRoot, Parchment);

            var lines = questLines;
            float lineHeight = LineHeight;
            float offset = 0f;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                ui.Add(new CuiLabel
                {
                    Text = { Text = line, FontSize = BodyFontSize, Align = TextAnchor.MiddleCenter, Color = "0.23 0.18 0.12 1" },
                    RectTransform = { AnchorMin = $"0 {offset}", AnchorMax = $"1 {offset + lineHeight}" }
                }, Parchment);
                offset += lineHeight;
            }

            currentTop -= ParchmentHeight;

            ui.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - GoalBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, UiRoot, GoalBar);

            ui.Add(new CuiLabel
            {
                Text = { Text = BuildGoalText(progress, quest), FontSize = GoalFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, GoalBar);

            CuiHelper.AddUi(player, ui);

            dukeUiVisible.Add(player.userID);
        }

        private void DrawQuestCompleteUI(BasePlayer player, QuestDefinition completedQuest)
        {
            CuiHelper.DestroyUi(player, QuestCompleteRoot);
            questCompleteVisible.Remove(player.userID);

            var c = new CuiElementContainer();
            float currentTop = 1f;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - QuestBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, "Hud", QuestCompleteRoot);

            c.Add(new CuiLabel
            {
                Text = { Text = "QUEST COMPLETE", FontSize = QuestFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, QuestCompleteRoot, QuestCompleteTop);

            currentTop -= QuestBarHeight;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.85 0.78 0.63 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - ParchmentHeight}", AnchorMax = $"1 {currentTop}" }
            }, QuestCompleteRoot, QuestCompleteParchment);

            c.Add(new CuiLabel
            {
                Text = { Text = completedQuest.Title, FontSize = BodyFontSize, Align = TextAnchor.MiddleCenter, Color = "0.23 0.18 0.12 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, QuestCompleteParchment);

            currentTop -= ParchmentHeight;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - GoalBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, QuestCompleteRoot, QuestCompleteBottom);

            c.Add(new CuiLabel
            {
                Text = { Text = "Next Quest", FontSize = GoalFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, QuestCompleteBottom);

            CuiHelper.AddUi(player, c);
        }

        private void ShowDukeQuestCompleteUI(BasePlayer player, QuestDefinition completedQuest)
        {
            CuiHelper.DestroyUi(player, DukeCompleteRoot);
            dukeCompleteVisible.Remove(player.userID);

            var c = new CuiElementContainer();
            float currentTop = 1f;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - QuestBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, "Hud", DukeCompleteRoot);

            c.Add(new CuiLabel
            {
                Text = { Text = "QUEST COMPLETE", FontSize = QuestFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, DukeCompleteRoot, QuestCompleteTop);

            currentTop -= QuestBarHeight;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.85 0.78 0.63 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - ParchmentHeight}", AnchorMax = $"1 {currentTop}" }
            }, DukeCompleteRoot, QuestCompleteParchment);

            c.Add(new CuiLabel
            {
                Text = { Text = completedQuest.Title, FontSize = BodyFontSize, Align = TextAnchor.MiddleCenter, Color = "0.23 0.18 0.12 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, QuestCompleteParchment);

            currentTop -= ParchmentHeight;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - GoalBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, DukeCompleteRoot, QuestCompleteBottom);

            c.Add(new CuiLabel
            {
                Text = { Text = "Next Quest", FontSize = GoalFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, QuestCompleteBottom);

            CuiHelper.AddUi(player, c);
        }

        private void PlayQuestCompleteSound(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            Effect.server.Run("assets/bundled/prefabs/fx/notice/loot.spawn.effect.prefab", player.transform.position);
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
                   "<color=#FFFFFF>The following quest chains have been unlocked: The Duke, Fisherman, Hunter, Diver, Lumberjack, Treasure Hunter, Bounty Hunter, Explorer, Barrel Smasher. Start one with /quest <b>NAME</b>.</color>\n" +
                   "<color=#AAAAAA>The questing system is still in a very early stage and many quest chains may not be available yet! Leave me feedback in Discord #bug-reports.</color>";
        }

        private List<string> BuildDescriptionLines(QuestDefinition quest)
        {
            var description = string.IsNullOrWhiteSpace(quest.Description)
                ? MissingDescriptionFallback
                : quest.Description.Trim();

            var segments = description.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>();

            foreach (var segment in segments)
            {
                lines.AddRange(WrapText(segment.Trim(), MaxCharsPerLine));
            }
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
            if (quest != null && quest.Id == DukeQuestFinalId && quest.Requirements.ContainsKey("duke_meet"))
            {
                return "Goal: Swear Fealty to The Duke!";
            }

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

            var queueAmountField = taskType.GetField("queueAmount");
            var completedAmountField = taskType.GetField("completedAmount");

            if (queueAmountField?.GetValue(task) is int queued
                && completedAmountField?.GetValue(task) is int completed)
            {
                return queued > completed;
            }

            var queueAmountProp = taskType.GetProperty("queueAmount");
            var completedAmountProp = taskType.GetProperty("completedAmount");

            if (queueAmountProp?.GetValue(task) is int queuedProp
                && completedAmountProp?.GetValue(task) is int completedProp)
            {
                return queuedProp > completedProp;
            }

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
                case "duke_meet":
                    return "Swear Fealty to The Duke!";
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
