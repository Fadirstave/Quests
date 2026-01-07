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
            public string ChainKey;
            public string CompletionMessage;
            public Action<BasePlayer> CompletionAction;
            public bool HasInventoryTrackedRequirements;
            public bool Completed;
            public bool RewardPending;
        }

        class QuestProgress
        {
            public int QuestId;
            public Dictionary<string, int> Progress = new Dictionary<string, int>();
            public Dictionary<string, int> InventoryBaseline = new Dictionary<string, int>();
            public bool Completed;
            public bool RewardPending;
            public bool Started;
            public bool StarterCompleted;
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
            ["basicblueprintfragment"] = "basicblueprintfragment",
            ["basic blueprint fragment"] = "basicblueprintfragment",
            ["basic blueprint"] = "basicblueprintfragment",
        };

        private static readonly HashSet<string> InventoryTrackedRequirementKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wood",
            "stones",
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
        private const float HotbarOffsetY = -0.05f;
        private const float UiMinOffsetY = -0.05f;

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
        private const string TidewardenChainName = "tidewarden";
        private const string HuntsmanChainName = "huntsman";
        private const ulong DukeNpcId = 8944230400;
        private const string DukeNpcName = "Duke of the Commonwealth";
        private const int DukeQuestFirstId = 1;
        private const int DukeQuestItemCount = 10;
        private const int DukeQuestFinalId = DukeQuestItemCount + 1;
        private const int DukeQuestIdOffset = 9000;
        private const string DukePriceCommand = "swear fealty";
        private const string EsquirePermission = "guishop.use";
        private const string EsquireTitlePermission = "quests.duke.esquire";
        private const int StarterQuestFinalId = 23;
        private const int TidewardenQuestStartId = 100;
        private const int HuntsmanQuestStartId = 200;
        private const string StarterChainKey = "starter";

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
                Title = "Quest I – Getting Started",
                Description = "Take up thy rock and gather wood and stone, the first toil of any survivor.",
                Requirements = new Dictionary<string, int>
                {
                    ["wood"] = 400,
                    ["stones"] = 200
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "woodtea", Amount = 1 },
                    new QuestReward { ShortName = "oretea", Amount = 1 }
                }
            };

            quests[2] = new QuestDefinition
            {
                Id = 2,
                Title = "Quest II — Stone Tools",
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
                Title = "Quest III — Gather Supplies",
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
                Title = "Quest IV — Builder’s Tools",
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
                Title = "Quest V — Claim and Build",
                Description = "Claim thy land and raise a humble shelter to rest within.",
                Requirements = new Dictionary<string, int>
                {
                    ["tc_auth"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "sleepingbag", Amount = 1 }
                }
            };

            quests[6] = new QuestDefinition
            {
                Id = 6,
                Title = "Quest VI — Storage",
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
                Title = "Quest VII — Secure the Door",
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
                Title = "Quest VIII — Cloth Gathering",
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
                Title = "Quest IX — Simple Clothing",
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
                Title = "Quest X — First Weapon",
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
                Title = "Quest XI — Hunting",
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
                Title = "Quest XII — Low Grade Fuel",
                Description = "Render fuel from beast and cloth to feed the flame.",
                Requirements = new Dictionary<string, int>
                {
                    ["lowgradefuel"] = 100
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "lowgradefuel", Amount = 100 }
                }
            };

            quests[13] = new QuestDefinition
            {
                Id = 13,
                Title = "Quest XIII — Furnace",
                Description = "Shape stone into a furnace for smelting ore.",
                Requirements = new Dictionary<string, int>
                {
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
                Title = "Quest XIV — Metal Ore",
                Description = "Mine metal ore from the earth and prepare to refine it.",
                Requirements = new Dictionary<string, int>
                {
                    ["metal.ore"] = 500
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "oretea", Amount = 1 }
                }
            };

            quests[15] = new QuestDefinition
            {
                Id = 15,
                Title = "Quest XV — Smelting",
                Description = "Smelt ore into metal fragments.",
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
                Title = "Quest XVI — Better Door",
                Description = "Forge a metal door and secure thy home.",
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
                Title = "Quest XVII — Repairs",
                Description = "Craft a repair bench and keep thy gear in shape.",
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
                Title = "Quest XVIII — Road Looting",
                Description = "Break barrels on the road and claim their spoils.",
                Requirements = new Dictionary<string, int>
                {
                    ["road.barrel"] = 6
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "scrap", Amount = 25 }
                }
            };

            quests[19] = new QuestDefinition
            {
                Id = 19,
                Title = "Quest XIX — Scrap Run",
                Description = "Gather scrap from the road and prepare for research.",
                Requirements = new Dictionary<string, int>
                {
                    ["scrap"] = 50
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "scrap", Amount = 25 }
                }
            };

            quests[20] = new QuestDefinition
            {
                Id = 20,
                Title = "Quest XX — Recycling",
                Description = "Use the recycler to break down unwanted items.",
                Requirements = new Dictionary<string, int>
                {
                    ["recycler_use"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "basicblueprintfragment", Amount = 2 }
                }
            };

            quests[21] = new QuestDefinition
            {
                Id = 21,
                Title = "Quest XXI — Workbench",
                Description = "Craft a workbench level 1.",
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
                Title = "Quest XXII — Research",
                Description = "Craft a research table to unlock blueprints.",
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
                Title = "Quest XXIII — Engineering",
                Description = "Craft an engineering workbench for advanced craft.",
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

            quests[TidewardenQuestStartId] = new QuestDefinition
            {
                Id = TidewardenQuestStartId,
                Title = "Tidewarden I — Gotta have rope",
                Description = "Collect rope for your fishing kit.",
                Requirements = new Dictionary<string, int>
                {
                    ["rope"] = 2
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "wood", Amount = 200 }
                },
                ChainKey = TidewardenChainName
            };

            quests[TidewardenQuestStartId + 1] = new QuestDefinition
            {
                Id = TidewardenQuestStartId + 1,
                Title = "Tidewarden II — Prepare your line",
                Description = "Craft a handmade fishing rod.",
                Requirements = new Dictionary<string, int>
                {
                    ["fishingrod.handmade"] = 1
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "worm", Amount = 10 }
                },
                ChainKey = TidewardenChainName
            };

            quests[TidewardenQuestStartId + 2] = new QuestDefinition
            {
                Id = TidewardenQuestStartId + 2,
                Title = "Tidewarden III — Cast your line",
                Description = "Catch 5 fish of any kind.",
                Requirements = new Dictionary<string, int>
                {
                    ["fish.any"] = 5
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "pie.survivors", Amount = 1 }
                },
                ChainKey = TidewardenChainName
            };

            quests[TidewardenQuestStartId + 3] = new QuestDefinition
            {
                Id = TidewardenQuestStartId + 3,
                Title = "Tidewarden IV — Harvest some fish",
                Description = "Gut your fish and collect raw fillets.",
                Requirements = new Dictionary<string, int>
                {
                    ["fish.raw"] = 20
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "bbq", Amount = 1 }
                },
                ChainKey = TidewardenChainName
            };

            quests[TidewardenQuestStartId + 4] = new QuestDefinition
            {
                Id = TidewardenQuestStartId + 4,
                Title = "Tidewarden V — Cook and eat your catch",
                Description = "Cook your fish and collect the meal.",
                Requirements = new Dictionary<string, int>
                {
                    ["fish.cooked"] = 5
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "pie.fish", Amount = 1 }
                },
                ChainKey = TidewardenChainName
            };

            quests[TidewardenQuestStartId + 5] = new QuestDefinition
            {
                Id = TidewardenQuestStartId + 5,
                Title = "Tidewarden VI — Better bait",
                Description = "Collect wolf meat for better bait.",
                Requirements = new Dictionary<string, int>
                {
                    ["wolfmeat.raw"] = 20
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "wolfmeat.raw", Amount = 20 }
                },
                ChainKey = TidewardenChainName
            };

            quests[TidewardenQuestStartId + 6] = new QuestDefinition
            {
                Id = TidewardenQuestStartId + 6,
                Title = "Tidewarden VII — Fisherman",
                Description = "Catch 20 trout.",
                Requirements = new Dictionary<string, int>
                {
                    ["fish.troutsmall"] = 20
                },
                Rewards = new List<QuestReward>(),
                ChainKey = TidewardenChainName,
                CompletionMessage = Prefix + "The tides now answer to your hand. You are named Tidewarden.",
                CompletionAction = GrantTidewardenRewards
            };

            quests[HuntsmanQuestStartId] = new QuestDefinition
            {
                Id = HuntsmanQuestStartId,
                Title = "Huntsman I — The essentials",
                Description = "Craft your hunting gear.",
                Requirements = new Dictionary<string, int>
                {
                    ["bow.hunting"] = 1,
                    ["knife.bone"] = 1,
                    ["arrow.wooden"] = 40
                },
                Rewards = new List<QuestReward>(),
                ChainKey = HuntsmanChainName
            };

            quests[HuntsmanQuestStartId + 1] = new QuestDefinition
            {
                Id = HuntsmanQuestStartId + 1,
                Title = "Huntsman II — First hunt",
                Description = "Choke 5 chickens.",
                Requirements = new Dictionary<string, int>
                {
                    ["chicken.kill"] = 5
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "easter.goldegg", Amount = 1 }
                },
                ChainKey = HuntsmanChainName
            };

            quests[HuntsmanQuestStartId + 2] = new QuestDefinition
            {
                Id = HuntsmanQuestStartId + 2,
                Title = "Huntsman III — Oh deer!",
                Description = "Slay 5 stags.",
                Requirements = new Dictionary<string, int>
                {
                    ["stag.kill"] = 5
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "pie.bigcat", Amount = 1 }
                },
                ChainKey = HuntsmanChainName
            };

            quests[HuntsmanQuestStartId + 3] = new QuestDefinition
            {
                Id = HuntsmanQuestStartId + 3,
                Title = "Huntsman IV — Press onward",
                Description = "Sharpen your instincts and prepare for the next hunt.",
                Requirements = new Dictionary<string, int>(),
                Rewards = new List<QuestReward>(),
                ChainKey = HuntsmanChainName
            };

            quests[HuntsmanQuestStartId + 4] = new QuestDefinition
            {
                Id = HuntsmanQuestStartId + 4,
                Title = "Huntsman V — Apex predator",
                Description = "Hunt down and slay a pack of wolves.",
                Requirements = new Dictionary<string, int>
                {
                    ["wolf.kill"] = 3
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "hat.wolf", Amount = 1 }
                },
                ChainKey = HuntsmanChainName
            };

            quests[HuntsmanQuestStartId + 5] = new QuestDefinition
            {
                Id = HuntsmanQuestStartId + 5,
                Title = "Huntsman VI — Forest predator",
                Description = "Slay the forest beasts.",
                Requirements = new Dictionary<string, int>
                {
                    ["bear.kill"] = 2
                },
                Rewards = new List<QuestReward>(),
                ChainKey = HuntsmanChainName
            };

            quests[HuntsmanQuestStartId + 6] = new QuestDefinition
            {
                Id = HuntsmanQuestStartId + 6,
                Title = "Huntsman VII — Frozen predator",
                Description = "Slay the frozen beasts.",
                Requirements = new Dictionary<string, int>
                {
                    ["polarbear.kill"] = 2
                },
                Rewards = new List<QuestReward>
                {
                    new QuestReward { ShortName = "pie.bigcat", Amount = 1 }
                },
                ChainKey = HuntsmanChainName
            };

            quests[HuntsmanQuestStartId + 7] = new QuestDefinition
            {
                Id = HuntsmanQuestStartId + 7,
                Title = "Huntsman VIII — The final hunt!",
                Description = "Slay the king of the jungle.",
                Requirements = new Dictionary<string, int>
                {
                    ["tiger.kill"] = 1
                },
                Rewards = new List<QuestReward>(),
                ChainKey = HuntsmanChainName,
                CompletionMessage = Prefix + "By blood and blade, you have earned the title: Huntsman.",
                CompletionAction = GrantHuntsmanRewards
            };

            foreach (var quest in quests.Values)
            {
                if (string.IsNullOrEmpty(quest.ChainKey) && quest.Id <= StarterQuestFinalId)
                {
                    quest.ChainKey = StarterChainKey;
                }

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
                Title = $"The Duke – Bring {displayName}",
                Description = "Bring the required item to prove thy worth.",
                Requirements = new Dictionary<string, int>
                {
                    [requirementItem] = 1
                },
                Rewards = new List<QuestReward>()
            };
        }

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

            if (!progress.StarterCompleted && progress.Completed && progress.QuestId == StarterQuestFinalId)
            {
                progress.StarterCompleted = true;
            }

            if (progress.Completed && quests.ContainsKey(progress.QuestId + 1))
            {
                progress.QuestId++;
                progress.Progress = new Dictionary<string, int>();
                progress.InventoryBaseline = new Dictionary<string, int>();
                progress.Completed = false;
                progress.RewardPending = false;
                SavePlayerData();
                InitializeInventoryBaseline(player, progress, quests[progress.QuestId]);
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

            var overrideKey = item.info.shortname == "box.wooden" ? "box.wooden.crafted" : null;
            var normalizedKey = NormalizeRequirementKey(item.info.shortname);

            HandleDukeCraftedItemAcquired(player, item.info.shortname, item.amount, overrideKey ?? normalizedKey);

            if (!TryGetActiveQuest(player, out var progress, out var quest))
                return;

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

            var normalizedKey = NormalizeRequirementKey(item.info.shortname);

            if (TryGetActiveQuest(player, out var progress, out var quest))
            {
                if (quest.HasInventoryTrackedRequirements)
                    UpdateInventoryTrackedProgress(player, progress, quest);

                if (ShouldIgnoreContainerGainForGatherQuest(quest, normalizedKey))
                    return;

                if (InventoryTrackedRequirementKeys.Contains(normalizedKey))
                    return;

                var requirementOverride = normalizedKey;
                if (quest.Requirements.ContainsKey("fish.any") && IsFishRequirement(normalizedKey))
                {
                    requirementOverride = "fish.any";
                }

                HandleQuestItemAcquired(player, progress, quest, item.info.shortname, item.amount, requirementOverride);
            }

            // Duke crafted-item quests should only advance from crafting completion.
        }

        private void OnItemStacked(Item item, Item target, int amount)
        {
            if (amount <= 0)
                return;

            var player = target?.parent?.GetOwnerPlayer()
                         ?? target?.GetOwnerPlayer()
                         ?? item?.GetOwnerPlayer()
                         ?? target?.parent?.entityOwner?.ToPlayer();
            if (player == null)
                return;

            var info = target?.info ?? item?.info;
            if (info?.shortname == null)
                return;

            var normalizedKey = NormalizeRequirementKey(info.shortname);

            if (!TryGetActiveQuest(player, out var progress, out var quest))
                return;

            if (quest.HasInventoryTrackedRequirements)
                UpdateInventoryTrackedProgress(player, progress, quest);

            if (ShouldIgnoreContainerGainForGatherQuest(quest, normalizedKey))
                return;

            if (InventoryTrackedRequirementKeys.Contains(normalizedKey))
                return;

            var requirementOverride = normalizedKey;
            if (quest.Requirements.ContainsKey("fish.any") && IsFishRequirement(normalizedKey))
            {
                requirementOverride = "fish.any";
            }

            HandleQuestItemAcquired(player, progress, quest, info.shortname, amount, requirementOverride);
        }

        private bool IsDukeQuestId(int questId) =>
            questId >= DukeQuestIdOffset + DukeQuestFirstId && questId <= DukeQuestIdOffset + DukeQuestFinalId;

        private int ToDukeQuestInternalId(int questId) => questId - DukeQuestIdOffset;

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
            else if (shortName.IndexOf("chicken", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                HandleQuestItemAcquired(player, "chicken.kill", 1);
            }
            else if (shortName.IndexOf("stag", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                HandleQuestItemAcquired(player, "stag.kill", 1);
            }
            else if (shortName.IndexOf("wolf", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                HandleQuestItemAcquired(player, "wolf.kill", 1);
            }
            else if (shortName.IndexOf("polarbear", StringComparison.OrdinalIgnoreCase) >= 0
                     || (shortName.IndexOf("polar", StringComparison.OrdinalIgnoreCase) >= 0
                         && shortName.IndexOf("bear", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                HandleQuestItemAcquired(player, "polarbear.kill", 1);
            }
            else if (shortName.IndexOf("bear", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                HandleQuestItemAcquired(player, "bear.kill", 1);
            }
            else if (shortName.IndexOf("tiger", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                HandleQuestItemAcquired(player, "tiger.kill", 1);
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

        private void HandleDukeCraftedItemAcquired(BasePlayer player, string shortName, int amount, string requirementKeyOverride = null)
        {
            if (!TryGetActiveDukeQuest(player, out var progress, out var quest))
                return;

            if (string.IsNullOrEmpty(shortName) || amount <= 0)
                return;

            var requirementKey = requirementKeyOverride ?? NormalizeRequirementKey(shortName);
            if (!quest.Requirements.TryGetValue(requirementKey, out var requiredAmount))
                return;

            if (!progress.Progress.ContainsKey(requirementKey))
                progress.Progress[requirementKey] = 0;

            progress.Progress[requirementKey] = Mathf.Min(
                progress.Progress[requirementKey] + amount,
                requiredAmount
            );

            DrawDukeUI(player, progress, quest);

            if (!HasMetAllRequirements(progress, quest))
                return;

            TryFinishDukeQuest(player, progress, quest);
        }

        private void EvaluateQuestProgressFulfillment(BasePlayer player, QuestProgress progress, QuestDefinition quest)
        {
            if (questUiVisible.Contains(player.userID))
                DrawQuestUi(player, progress, quest);

            if (!HasMetAllRequirements(progress, quest))
                return;

            TryFinishQuest(player, progress, quest);
        }

        private void UpdateInventoryTrackedProgress(BasePlayer player, QuestProgress progress, QuestDefinition quest)
        {
            if (player == null || quest == null || !quest.HasInventoryTrackedRequirements)
                return;

            InitializeInventoryBaseline(player, progress, quest);

            foreach (var req in quest.Requirements)
            {
                var normalizedKey = NormalizeRequirementKey(req.Key);
                if (!InventoryTrackedRequirementKeys.Contains(normalizedKey)) continue;

                var haveNow = GetItemAmount(player, req.Key);
                var baseline = progress.InventoryBaseline.TryGetValue(req.Key, out var v) ? v : 0;

                var delta = Mathf.Max(0, haveNow - baseline);
                var currentProgress = progress.Progress.TryGetValue(req.Key, out var current) ? current : 0;
                progress.Progress[req.Key] = Mathf.Min(Mathf.Max(currentProgress, delta), req.Value);
            }

            EvaluateQuestProgressFulfillment(player, progress, quest);
        }

        private void InitializeInventoryBaseline(BasePlayer player, QuestProgress progress, QuestDefinition quest)
        {
            if (player == null || quest == null || !quest.HasInventoryTrackedRequirements)
                return;

            if (progress.InventoryBaseline == null)
                progress.InventoryBaseline = new Dictionary<string, int>();

            foreach (var req in quest.Requirements)
            {
                var normalizedKey = NormalizeRequirementKey(req.Key);
                if (!InventoryTrackedRequirementKeys.Contains(normalizedKey)) continue;

                if (progress.InventoryBaseline.ContainsKey(req.Key))
                    continue;

                var currentAmount = GetItemAmount(player, req.Key);
                progress.InventoryBaseline[req.Key] = currentAmount;
            }
        }

        private bool HasMetAllRequirements(QuestProgress progress, QuestDefinition quest)
        {
            foreach (var req in quest.Requirements)
            {
                if (!progress.Progress.TryGetValue(req.Key, out var current))
                    return false;

                if (current < req.Value)
                    return false;
            }

            return true;
        }

        private int GetItemAmount(BasePlayer player, string shortName)
        {
            var definition = FindItemDefinitionWithFallback(shortName);
            if (definition == null) return 0;

            return player.inventory.GetAmount(definition.itemid);
        }

        private Item FindItemInInventories(BasePlayer player, string shortName)
        {
            if (player == null || string.IsNullOrEmpty(shortName))
                return null;

            var definition = FindItemDefinitionWithFallback(shortName);

            Item FindInContainer(ItemContainer container)
            {
                if (container == null)
                    return null;

                foreach (var item in container.itemList)
                {
                    if (item?.info == null)
                        continue;

                    if (definition != null)
                    {
                        if (item.info == definition)
                            return item;
                    }
                    else if (item.info.shortname.Equals(shortName, StringComparison.OrdinalIgnoreCase))
                    {
                        return item;
                    }
                }

                return null;
            }

            return FindInContainer(player.inventory?.containerMain)
                ?? FindInContainer(player.inventory?.containerBelt)
                ?? FindInContainer(player.inventory?.containerWear);
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
                    progress.InventoryBaseline = new Dictionary<string, int>();
                    progress.Completed = false;
                    progress.RewardPending = false;
                    SavePlayerData();

                    SendReply(player, Prefix + $"Next quest unlocked: {quests[progress.QuestId].Title}");
                    InitializeInventoryBaseline(player, progress, quests[progress.QuestId]);
                }
                else
                {
                    progress.Completed = true;
                    progress.RewardPending = false;
                    if (quest.ChainKey == StarterChainKey)
                    {
                        progress.StarterCompleted = true;
                    }
                    SavePlayerData();

                    if (quest.CompletionAction != null)
                    {
                        quest.CompletionAction(player);
                    }

                    if (!string.IsNullOrEmpty(quest.CompletionMessage))
                    {
                        SendReply(player, quest.CompletionMessage);
                    }
                    else if (quest.ChainKey == StarterChainKey)
                    {
                        SendReply(player, BuildStarterCompletionMessage());
                    }
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

                    if (hasNextQuest)
                    {
                        timer.Once(UiSwapDelay, () =>
                        {
                            if (player == null || !player.IsConnected) return;
                            CuiHelper.DestroyUi(player, QuestCompleteRoot);
                            questCompleteVisible.Remove(player.userID);
                            DrawUI(player);
                            questUiVisible.Add(player.userID);
                        });
                    }
                    else
                    {
                        timer.Once(UiSwapDelay, () =>
                        {
                            if (player == null || !player.IsConnected) return;
                            CuiHelper.DestroyUi(player, QuestCompleteRoot);
                            questCompleteVisible.Remove(player.userID);
                        });
                    }
                });
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

        private void GrantEsquireRewards(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            permission.AddUserGroup(player.UserIDString, "esquire");
            permission.GrantUserPermission(player.UserIDString, EsquirePermission, this);
            permission.GrantUserPermission(player.UserIDString, EsquireTitlePermission, this);
        }

        private void GrantTidewardenRewards(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            permission.AddUserGroup(player.UserIDString, "tidewarden");
        }

        private void GrantHuntsmanRewards(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            permission.AddUserGroup(player.UserIDString, "huntsman");
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

        private int GetRequiredSlots(ItemDefinition def, int amount)
        {
            if (def == null || amount <= 0)
                return 0;

            int maxStack = def.stackable > 0 ? def.stackable : 1;
            int fullStacks = amount / maxStack;
            int remaining = amount % maxStack;

            return fullStacks + (remaining > 0 ? 1 : 0);
        }

        private int GetRemainingStackSlots(ItemContainer container, ItemDefinition def, int amount)
        {
            if (container == null || def == null || amount <= 0)
                return amount;

            int remaining = amount;
            int maxStack = def.stackable > 0 ? def.stackable : 1;

            foreach (var item in container.itemList)
            {
                if (item == null || item.info != def)
                    continue;

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

        [ConsoleCommand("quests.complete.duke.next")]
        private void CmdQuestCompleteDukeNext(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            CuiHelper.DestroyUi(player, DukeCompleteRoot);
            dukeCompleteVisible.Remove(player.userID);

            if (TryGetActiveDukeQuest(player, out var dukeProgress, out var dukeQuest))
            {
                DrawDukeUI(player, dukeProgress, dukeQuest);
                dukeUiVisible.Add(player.userID);
            }
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
                progress.InventoryBaseline = new Dictionary<string, int>();
                SavePlayerData();
            }

            EnsureStarterQuestCompletedForUnlockedChains(player, progress);
            if (quests.ContainsKey(progress.QuestId))
            {
                InitializeInventoryBaseline(player, progress, quests[progress.QuestId]);
            }

            if (args != null && args.Length > 0)
            {
                if (!progress.StarterCompleted)
                {
                    SendReply(player, Prefix + "Complete the starter quest before starting quest chains.");
                    return;
                }

                if (progress.RewardPending)
                {
                    SendReply(player, Prefix + "Claim your pending reward before starting a new quest chain.");
                    return;
                }

                if (TryGetActiveQuest(player, out _, out _))
                {
                    SendReply(player, Prefix + "Finish your current quest before starting a new quest chain.");
                    return;
                }

                var input = string.Join(" ", args).Trim();
                if (IsDukePriceCommand(input))
                {
                    TryTurnInDukePrice(player);
                    return;
                }

                if (IsDukeChain(input))
                {
                    StartDukeQuest(player);
                    return;
                }

                if (TryStartQuestChain(player, progress, input))
                {
                    return;
                }

                SendReply(player, Prefix + DevPlaceholderMsg);
                return;
            }

            if (questCompleteVisible.Contains(player.userID))
            {
                CuiHelper.DestroyUi(player, QuestCompleteRoot);
                questCompleteVisible.Remove(player.userID);
                return;
            }

            if (dukeCompleteVisible.Contains(player.userID))
            {
                CuiHelper.DestroyUi(player, DukeCompleteRoot);
                dukeCompleteVisible.Remove(player.userID);
                return;
            }

            if (dukeUiVisible.Contains(player.userID))
            {
                CuiHelper.DestroyUi(player, UiRoot);
                dukeUiVisible.Remove(player.userID);
                return;
            }

            if (questUiVisible.Contains(player.userID))
            {
                CuiHelper.DestroyUi(player, UiRoot);
                questUiVisible.Remove(player.userID);
                return;
            }

            if (TryGetActiveDukeQuest(player, out var dukeProgress, out var dukeQuest))
            {
                DrawDukeUI(player, dukeProgress, dukeQuest);
                dukeUiVisible.Add(player.userID);
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

        private bool IsDukeChain(string chainName)
        {
            if (string.IsNullOrWhiteSpace(chainName))
            {
                return false;
            }

            return chainName.Equals("duke", StringComparison.OrdinalIgnoreCase)
                || chainName.Equals(DukeChainName, StringComparison.OrdinalIgnoreCase)
                || chainName.Replace(" ", string.Empty).Equals(DukeChainName, StringComparison.OrdinalIgnoreCase);
        }

        private bool TryStartQuestChain(BasePlayer player, QuestProgress progress, string input)
        {
            if (player == null || progress == null || string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var normalized = input.Trim().Replace(" ", string.Empty);

            if (normalized.Equals(TidewardenChainName, StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("fisherman", StringComparison.OrdinalIgnoreCase))
            {
                StartQuestChain(player, progress, TidewardenQuestStartId);
                return true;
            }

            if (normalized.Equals(HuntsmanChainName, StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("hunter", StringComparison.OrdinalIgnoreCase))
            {
                StartQuestChain(player, progress, HuntsmanQuestStartId);
                return true;
            }

            return false;
        }

        private void StartQuestChain(BasePlayer player, QuestProgress progress, int questId)
        {
            if (!quests.ContainsKey(questId))
            {
                SendReply(player, Prefix + "That quest chain is not available yet.");
                return;
            }

            progress.QuestId = questId;
            progress.Progress = new Dictionary<string, int>();
            progress.InventoryBaseline = new Dictionary<string, int>();
            progress.Completed = false;
            progress.RewardPending = false;
            progress.Started = true;
            SavePlayerData();

            CuiHelper.DestroyUi(player, UiRoot);
            questUiVisible.Remove(player.userID);

            InitializeInventoryBaseline(player, progress, quests[progress.QuestId]);
            DrawUI(player);
            questUiVisible.Add(player.userID);
        }

        private void EnsureStarterQuestCompletedForUnlockedChains(BasePlayer player, QuestProgress progress)
        {
            if (player == null || progress == null || progress.Completed)
                return;

            if (!dukeQuests.TryGetValue(player.userID, out var dukeProgress))
                return;

            if (!dukeProgress.Started && !dukeProgress.Completed && dukeProgress.QuestId <= DukeQuestFirstId)
                return;

            progress.Completed = true;
            progress.RewardPending = false;
            progress.StarterCompleted = true;
            SavePlayerData();
        }

        private bool IsDukePriceCommand(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            var normalized = input.Trim().ToLowerInvariant().Replace("’", "'");
            return normalized.Equals(DukePriceCommand, StringComparison.OrdinalIgnoreCase);
        }

        private void StartDukeQuest(BasePlayer player)
        {
            var progress = GetDukeProgress(player);
            var quest = BuildDukeQuest(progress.QuestId);

            if (progress.Completed)
            {
                SendReply(player, Prefix + "Thou hast already earned the favor of The Duke.");
                return;
            }

            if (!progress.Started)
            {
                progress.Started = true;
                SaveDukeData();
            }

            CuiHelper.DestroyUi(player, UiRoot);
            questUiVisible.Remove(player.userID);

            DrawDukeUI(player, progress, quest);
            dukeUiVisible.Add(player.userID);
        }

        private bool HasDukeOfferings(BasePlayer player)
        {
            if (player == null)
                return false;

            foreach (var itemName in DukeRequiredItems)
            {
                if (FindItemInInventories(player, itemName) == null)
                {
                    return false;
                }
            }

            return true;
        }

        private void RemoveDukeOfferings(BasePlayer player)
        {
            foreach (var itemName in DukeRequiredItems)
            {
                var item = FindItemInInventories(player, itemName);
                if (item != null)
                {
                    item.Remove(1);
                }
            }
        }

        private void TryTurnInDukePrice(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            if (!TryGetActiveDukeQuest(player, out var progress, out var quest) || quest.Id != DukeQuestFinalId)
            {
                SendReply(player, Prefix + "must complete \"The Duke\" Quest Chain");
                return;
            }

            if (!HasDukeOfferings(player))
            {
                SendReply(player, "You have not yet gathered all offerings for the Duke.");
                return;
            }

            RemoveDukeOfferings(player);
            progress.Progress["duke_meet"] = 1;
            SaveDukeData();

            if (dukeUiVisible.Contains(player.userID))
            {
                DrawDukeUI(player, progress, quest);
            }

            TryFinishDukeQuest(player, progress, quest);
        }

        // =========================
        // ADMIN COMMANDS
        // =========================
        [ChatCommand("questreset")]
        private void CmdQuestReset(BasePlayer player, string cmd, string[] args)
        {
            if (!IsAdmin(player))
            {
                SendReply(player, Prefix + "You are not an admin.");
                return;
            }

            if (args.Length == 0)
            {
                var progress = GetProgress(player);
                if (TryGetActiveDukeQuest(player, out var dukeProgress, out _))
                {
                    ResetDukeQuest(player, dukeProgress.QuestId);
                    SendReply(player, Prefix + $"Reset The Duke quest {DukeQuestIdOffset + dukeProgress.QuestId} for {player.displayName}.");
                    return;
                }

                ResetQuest(player, progress.QuestId);
                SendReply(player, Prefix + $"Reset quest {progress.QuestId} for {player.displayName}.");
                return;
            }

            var target = ResolvePlayerTarget(player, args.Length > 1 ? args[1] : args[0]);
            if (target == null)
            {
                SendReply(player, Prefix + "Player not found.");
                return;
            }

            int targetQuest;
            if (args.Length > 1 && !IsDukeChain(args[0]))
            {
                if (!int.TryParse(args[0], out targetQuest))
                {
                    SendReply(player, Prefix + "Usage: /questreset <questId|The Duke> [player name|steamId|me]");
                    return;
                }
            }
            else
            {
                if (!int.TryParse(args.Length > 1 ? args[1] : args[0], out targetQuest))
                {
                    targetQuest = 0;
                }
            }

            if (args.Length > 1)
            {
                if (IsDukeChain(args[0]))
                {
                    ResetDukeQuest(target, ToDukeQuestInternalId(targetQuest));
                    SendReply(player, Prefix + $"Reset The Duke quest chain for {target.displayName}.");
                }
                else
                {
                    ResetQuest(target, targetQuest);
                    SendReply(player, Prefix + $"Reset quest {targetQuest} for {target.displayName}.");
                }
                return;
            }

            if (IsDukeChain(args[0]))
            {
                ResetDukeQuest(target, ToDukeQuestInternalId(targetQuest));
                SendReply(player, Prefix + $"Reset The Duke quest chain for {target.displayName}.");
                return;
            }

            ResetQuest(target, targetQuest);
            SendReply(player, Prefix + $"Reset quest {targetQuest} for {target.displayName}.");
        }

        private void ResetQuest(BasePlayer target, int questId)
        {
            if (!quests.ContainsKey(questId))
            {
                SendReply(target, Prefix + $"Quest {questId} not found.");
                return;
            }

            var progress = GetProgress(target);
            progress.QuestId = questId;
            progress.Progress = new Dictionary<string, int>();
            progress.InventoryBaseline = new Dictionary<string, int>();
            progress.Completed = false;
            progress.RewardPending = false;
            progress.Started = true;
            SavePlayerData();

            if (questUiVisible.Contains(target.userID))
            {
                CuiHelper.DestroyUi(target, UiRoot);
                questUiVisible.Remove(target.userID);
                DrawUI(target);
                questUiVisible.Add(target.userID);
            }
        }

        private void ResetDukeQuest(BasePlayer target, int questId)
        {
            if (questId < DukeQuestFirstId || questId > DukeQuestFinalId)
                questId = DukeQuestFirstId;

            var quest = BuildDukeQuest(questId);
            dukeQuests[target.userID] = new QuestProgress { QuestId = quest.Id, Started = true };
            SaveDukeData();

            if (dukeUiVisible.Contains(target.userID))
            {
                CuiHelper.DestroyUi(target, UiRoot);
                dukeUiVisible.Remove(target.userID);
                DrawDukeUI(target, GetDukeProgress(target), quest);
                dukeUiVisible.Add(target.userID);
            }
        }

        [ChatCommand("questcomplete")]
        private void CmdQuestComplete(BasePlayer player, string cmd, string[] args)
        {
            if (!IsAdmin(player))
            {
                SendReply(player, Prefix + "You are not an admin.");
                return;
            }

            if (args.Length == 0)
            {
                var progress = GetProgress(player);
                if (TryGetActiveDukeQuest(player, out var dukeProgress, out _))
                {
                    ForceCompleteDukeQuest(player, dukeProgress.QuestId);
                    SendReply(player, Prefix + $"Completed The Duke quest {DukeQuestIdOffset + dukeProgress.QuestId} for {player.displayName}.");
                    return;
                }

                ForceCompleteQuest(player, progress.QuestId);
                SendReply(player, Prefix + $"Completed quest {progress.QuestId} for {player.displayName}.");
                return;
            }

            var target = ResolvePlayerTarget(player, args.Length > 1 ? args[1] : args[0]);
            if (target == null)
            {
                SendReply(player, Prefix + "Player not found.");
                return;
            }

            int targetQuest;
            if (args.Length > 1 && !IsDukeChain(args[0]))
            {
                if (!int.TryParse(args[0], out targetQuest))
                {
                    SendReply(player, Prefix + "Usage: /questcomplete <questId|The Duke> [player name|steamId|me]");
                    return;
                }
            }
            else
            {
                if (!int.TryParse(args.Length > 1 ? args[1] : args[0], out targetQuest))
                {
                    targetQuest = 0;
                }
            }

            if (args.Length > 1)
            {
                if (IsDukeChain(args[0]))
                {
                    ForceCompleteDukeQuest(target, ToDukeQuestInternalId(targetQuest));
                    SendReply(player, Prefix + $"Completed The Duke quest {DukeQuestIdOffset + targetQuest} for {target.displayName}.");
                }
                else
                {
                    ForceCompleteQuest(target, targetQuest);
                    SendReply(player, Prefix + $"Completed quest {targetQuest} for {target.displayName}.");
                }
                return;
            }

            if (IsDukeChain(args[0]))
            {
                ForceCompleteDukeQuest(target, ToDukeQuestInternalId(targetQuest));
                SendReply(player, Prefix + $"Completed The Duke quest {DukeQuestIdOffset + targetQuest} for {target.displayName}.");
                return;
            }

            ForceCompleteQuest(target, targetQuest);
            SendReply(player, Prefix + $"Completed quest {targetQuest} for {target.displayName}.");
        }

        private void ForceCompleteQuest(BasePlayer target, int questId)
        {
            if (!quests.TryGetValue(questId, out var quest))
            {
                SendReply(target, Prefix + $"Quest {questId} not found.");
                return;
            }

            questCompleteVisible.Remove(target.userID);

            var progress = GetProgress(target);
            progress.QuestId = quest.Id;
            progress.Progress = new Dictionary<string, int>();
            progress.InventoryBaseline = new Dictionary<string, int>();
            progress.Completed = false;
            progress.RewardPending = false;
            progress.Started = true;

            foreach (var req in quest.Requirements)
            {
                progress.Progress[req.Key] = req.Value;
            }

            TryFinishQuest(target, progress, quest);
        }

        private void ForceCompleteDukeQuest(BasePlayer target, int questId)
        {
            if (target == null) return;

            var quest = BuildDukeQuest(questId);

            CuiHelper.DestroyUi(target, DukeCompleteRoot);
            dukeCompleteVisible.Remove(target.userID);

            var progress = GetDukeProgress(target);
            progress.QuestId = quest.Id;
            progress.Progress = new Dictionary<string, int>();
            progress.Started = true;
            progress.Completed = false;
            progress.RewardPending = false;

            foreach (var req in quest.Requirements)
            {
                progress.Progress[req.Key] = req.Value;
            }

            SaveDukeData();

            TryFinishDukeQuest(target, progress, quest);
        }

        // =========================
        // UI
        // =========================
        private void DrawUI(BasePlayer player)
        {
            var progress = GetProgress(player);
            if (!quests.TryGetValue(progress.QuestId, out var quest))
            {
                SendReply(player, Prefix + DevPlaceholderMsg);
                return;
            }

            if (quest.HasInventoryTrackedRequirements)
            {
                InitializeInventoryBaseline(player, progress, quest);
                UpdateInventoryTrackedProgress(player, progress, quest);
            }

            if (HasMetAllRequirements(progress, quest))
            {
                TryFinishQuest(player, progress, quest);
                return;
            }

            DrawQuestUi(player, progress, quest);
        }

        private void DrawDukeUI(BasePlayer player, QuestProgress progress, QuestDefinition quest)
        {
            DrawQuestUi(player, progress, quest);
        }

        private void DrawQuestUi(BasePlayer player, QuestProgress progress, QuestDefinition quest)
        {
            CuiHelper.DestroyUi(player, UiRoot);

            var lines = BuildDescriptionLines(quest);

            float parchmentHeight = Mathf.Max(LineHeight * 2, lines.Count * LineHeight);
            float totalHeight = QuestBarHeight + parchmentHeight + GoalBarHeight;
            float offsetY = UiMinOffsetY + HotbarOffsetY;

            var c = new CuiElementContainer();

            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = $"0.38 {offsetY}", AnchorMax = $"0.61 {offsetY + totalHeight}" }
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

            c.Add(new CuiLabel
            {
                Text = { Text = string.Join("\n", lines), FontSize = BodyFontSize, Align = TextAnchor.MiddleCenter, Color = "0.23 0.18 0.12 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, Parchment);

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

            float parchmentHeight = LineHeight * 2;
            float totalHeight = QuestBarHeight + parchmentHeight + GoalBarHeight;
            float offsetY = UiMinOffsetY + HotbarOffsetY;

            var c = new CuiElementContainer();

            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = $"0.38 {offsetY}", AnchorMax = $"0.61 {offsetY + totalHeight}" }
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
                Text = { Text = completedQuest.Title, FontSize = BodyFontSize, Align = TextAnchor.MiddleCenter, Color = "0.23 0.18 0.12 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, QuestCompleteParchment);

            currentTop -= parchmentHeight;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - GoalBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, QuestCompleteRoot, QuestCompleteBottom);

            c.Add(new CuiLabel
            {
                Text = { Text = "Quest Complete", FontSize = GoalFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, QuestCompleteBottom, "QuestsUI.CompleteBottom.Label");

            CuiHelper.AddUi(player, c);
        }

        private void ShowDukeQuestCompleteUI(BasePlayer player, QuestDefinition completedQuest)
        {
            CuiHelper.DestroyUi(player, DukeCompleteRoot);

            float parchmentHeight = LineHeight * 2;
            float totalHeight = QuestBarHeight + parchmentHeight + GoalBarHeight;
            float offsetY = UiMinOffsetY + HotbarOffsetY;

            var c = new CuiElementContainer();

            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = $"0.38 {offsetY}", AnchorMax = $"0.61 {offsetY + totalHeight}" },
                CursorEnabled = true
            }, "Hud", DukeCompleteRoot);

            float currentTop = 1f;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - QuestBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, DukeCompleteRoot, QuestCompleteTop);

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
            }, DukeCompleteRoot, QuestCompleteParchment);

            c.Add(new CuiLabel
            {
                Text = { Text = completedQuest.Title, FontSize = BodyFontSize, Align = TextAnchor.MiddleCenter, Color = "0.23 0.18 0.12 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, QuestCompleteParchment);

            currentTop -= parchmentHeight;

            c.Add(new CuiPanel
            {
                Image = { Color = $"0.48 0.12 0.12 {UiAlpha}" },
                RectTransform = { AnchorMin = $"0 {currentTop - GoalBarHeight}", AnchorMax = $"1 {currentTop}" }
            }, DukeCompleteRoot, QuestCompleteBottom);

            c.Add(new CuiButton
            {
                Button = { Color = "0 0 0 0", Command = "quests.complete.duke.next", Close = DukeCompleteRoot },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                Text = { Text = "Next Quest", FontSize = GoalFontSize, Align = TextAnchor.MiddleCenter, Color = "0.95 0.91 0.85 1" }
            }, QuestCompleteBottom, "QuestsUI.CompleteBottom.Button");

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
                   "<color=#FFFFFF>The following quest chains have been unlocked: The Duke, Tidewarden, Huntsman, Diver, Lumberjack, Treasure Hunter, Bounty Hunter, Explorer, Barrel Smasher. Start one with /quest <b>NAME</b>.</color>\n" +
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
                case "iotable":
                    return "iotable";
                default:
                    return shortName;
            }
        }

        private bool IsFishRequirement(string shortName)
        {
            if (string.IsNullOrWhiteSpace(shortName))
                return false;

            switch (shortName)
            {
                case "fish.anchovy":
                case "fish.catfish":
                case "fish.herring":
                case "fish.minnows":
                case "fish.orangeroughy":
                case "fish.salmon":
                case "fish.sardine":
                case "fish.smallshark":
                case "fish.troutsmall":
                case "fish.yellowperch":
                    return true;
                default:
                    return false;
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
                case "chicken.kill":
                    return "Slay Chickens";
                case "stag.kill":
                    return "Slay Stags";
                case "wolf.kill":
                    return "Slay Wolves";
                case "bear.kill":
                    return "Slay Bears";
                case "polarbear.kill":
                    return "Slay Polar Bears";
                case "tiger.kill":
                    return "Slay Tigers";
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
                case "fish.any":
                    return "Catch any fish";
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
