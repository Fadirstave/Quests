using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Rust;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Facepunch;
using Oxide.Core.Libraries;
using UnityEngine;
using UnityEngine.Networking;
using Oxide.Plugins.QuestsExtensionMethods;

namespace Oxide.Plugins
{
	[Info("Quests", "Fadir Stave", "1.0.0")]
	[Description("Supper dupper much more better'r questing plugin")]
	public class Quests : RustPlugin 
	{
		#region ReferencePlugins

		[PluginReference] Plugin RaidableBases;

		private void SendChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel = ConVar.Chat.ChatChannel.Global)
		{
			player.SendConsoleCommand("chat.add", channel, 0, FormatMessageText(message, true));
		}

		private string FormatMessageText(string message, bool withPrefix = false)
		{
			if (string.IsNullOrEmpty(message) || _config?.textStyleSettings == null || !_config.textStyleSettings.useTextStyleCustomization)
				return message;

			string styled = message;
			styled = ReplaceColor(styled, "4286f4", _config.textStyleSettings.primaryColorHex);
			styled = ReplaceColor(styled, "42a1f5", _config.textStyleSettings.secondaryColorHex);

			if (withPrefix && _config.textStyleSettings.useChatPrefix && !string.IsNullOrWhiteSpace(_config.textStyleSettings.chatPrefix))
				styled = _config.textStyleSettings.chatPrefix + styled;

			if (withPrefix && _config.textStyleSettings.useBold)
				styled = $"<b>{styled}</b>";

			if (withPrefix && _config.textStyleSettings.useItalic)
				styled = $"<i>{styled}</i>";

			return styled;
		}

		private static string ReplaceColor(string message, string sourceHex, string targetHex)
		{
			if (string.IsNullOrWhiteSpace(targetHex))
				return message;

			return Regex.Replace(message, $"<color=#{sourceHex}>", $"<color=#{targetHex}>", RegexOptions.IgnoreCase);
		}

		private bool IsTeammate(ulong userID, ulong targetID)
		{
			return RelationshipManager.ServerInstance.playerToTeam.TryGetValue(userID, out RelationshipManager.PlayerTeam team) && team.members.Contains(targetID);
		}

		#endregion

		#region Variables

		public static Quests? Instance;
		private ImageUI _imageUI;

		private Dictionary<long, Quest> _questList = new();

		private Dictionary<ulong, PlayerData> _playersInfo = new();
		private QuestStatistics _questStatistics = new();
		private const string PlayerDataFileName = "Quests/PlayerData";
		private const string StatisticsDataFileName = "Quests/Statistics";

		private class PlayerData
		{
			public readonly List<long> CompletedQuestIds = new();
			public readonly Dictionary<long, double> PlayerQuestCooldowns = new();
			public readonly List<PlayerQuest> CurrentPlayerQuests = new();

			public double? GetCooldownForQuest(long questId)
			{
				return PlayerQuestCooldowns.TryGetValue(questId, out double cooldown) ? cooldown : (double?)null;
			}
		}

		#endregion

		#region Lang

		protected override void LoadDefaultMessages()
		{
			lang.RegisterMessages(new Dictionary<string, string>
			{
				["QUESTS_CopyPasteError"] = "There was a problem with CopyPaste! Contact the Developer!",
				["QUESTS_CopyPasteSuccessfully"] = "The building has spawned successfully!",
				["QUESTS_BuildingPasteError"] = "There was a problem with spawning the Building! Contact the Developer!",
				["QUESTS_MissingOutPost"] = "Your map doesnt have an Outpost Monument. Please use a custom spawn point.",
				["QUESTS_MissingQuests"]
					= "You do not have a file with tasks, the plugin will not work correctly! Create one on the Website - https://quests.skyplugins.ru/ or use the included one.",
				["QUESTS_FileNotLoad"] = "The construction file was not found : {0}. Move it to the copy paste folder",
				["QUESTS_UI_TASKLIST"] = "Quest List",
				["QUESTS_UI_Awards"] = "Rewards",
				["QUESTS_UI_TASKCount"] = "<color=#42a1f5>{0}</color> QUESTS",
				["QUESTS_UI_CHIPperformed"] = "Completed",
				["QUESTS_UI_CHIPInProgress"] = "In progress",
				["QUESTS_UI_QUESTREPEATCAN"] = "Yes",
				["QUESTS_UI_QUESTREPEATfForbidden"] = "No",
				["QUESTS_UI_Missing"] = "Missing",
				["QUESTS_UI_InfoRepeatInCD"] = "Repeat {0}  |  Cooldown {1}  |  Hand in {2}",
				["QUESTS_UI_QuestNecessary"] = "Needed",
				["QUESTS_UI_QuestNotNecessary"] = "Not needed",
				["QUESTS_UI_QuestBtnPerformed"] = "COMPLETED",
				["QUESTS_UI_QuestBtnTake"] = "TAKE",
				["QUESTS_UI_QuestBtnPass"] = "COMPLETE",
				["QUESTS_UI_QuestBtnDelivery"] = "DELIVER",
				["QUESTS_UI_QuestBtnRefuse"] = "REFUSE",
				["QUESTS_UI_ACTIVEOBJECTIVES"] = "Objective: {0}",
				["QUESTS_UI_MiniQLInfo"] = "{0}\nProgress: {1} / {2}\nQuest: {3}",
				["QUESTS_UI_MiniQLInfoDelivery"] = "{0}\nQuest: {3}",
				["QUESTS_UI_CMDPosChange"]
					= "You have successfully changed the position for building within the Outpost.\n(You need to reload the plugin)\nYou can configure the building's rotation in the config",
				["QUESTS_UI_CMDCustomPosAdd"]
					= "You have successfully added a custom building position.\n(You need to reload the plugin)\nYou can rotate the building in the config!\nRemember to enable the option to spawn a building on a custom position in the config.",
				["QUESTS_UI_QuestLimit"] = "You have to many <color=#4286f4>unfinished</color> Quests",
				["QUESTS_UI_AlreadyTaken"] = "You have already <color=#4286f4>taken</color> this Quest!",
				["QUESTS_UI_NotPerm"] = "You do not have the rights to perform this Quest.",
				["QUESTS_UI_AlreadyDone"] = "You have already <color=#4286f4>completed</color> this Quest!",
				["QUESTS_UI_TookTasks"] = "You have <color=#4286f4>successfully</color> accepted the Quest {0}",
				["QUESTS_UI_ACTIVECOLDOWN"] = "This Quest is on Cooldown.",
				["QUESTS_UI_LackOfSpace"] = "Your inventory is full! Clear some space and try again!",
				["QUESTS_UI_QuestsCompleted"] = "Quest Completed! Enjoy your reward!",
				["QUESTS_UI_PassedTasks"] = "So this Quest was to much for you? \n Try again later!",
				["QUESTS_UI_ActiveQuestCount"] = "You have no active Quests.",
				["QUESTS_Finished_QUEST"] = "You have completed the task: <color=#4286f4>{0}</color>",
				["QUESTS_Finished_QUEST_ALL"] = "Player <color=#4286f4>{0}</color> just completed a task: <color=#4286f4>{1}</color> and got a reward!",
				["QUESTS_UI_InsufficientResources"] = "You don't have {0}, you should definitely bring this to Sidorovich",
				["QUESTS_UI_InsufficientResourcesSkin"] = "You don’t have the required item, you need to bring it to Sidorovich",
				["QUESTS_UI_NotResourcesAmount"] = "You don't have enough {0}, you need {1}",
				["QUESTS_SoundLoadErrorExt"] = "The voice file {0} is missing, upload it using this path - (/data/Quests/Sounds). Or remove it from the configuration",
				["QUESTS_UI_CATEGORY"] = "CATEGORIES",
				["QUESTS_UI_CATEGORY_ONE"] = "Available tasks",
				["QUESTS_UI_CATEGORY_TWO"] = "Active tasks",
				["QUESTS_UI_TASKS_LIST_EMPTY"] = "Quest list is empty",
				["QUESTS_UI_TASKS_INFO_EMPTY"] = "Select a task to see information about it",
				["QUESTS_REPEATABLE_QUEST_AVAILABLE_AGAIN"] = "You can participate in the quest \"<color=#4286f4>{0}</color>\" again! \nDon't miss your chance!",
				["QUESTS_INSUFFICIENT_PERMISSIONS_ERROR"] = "You don't have sufficient permissions to use this command.",
				["QUESTS_COMMAND_SYNTAX_ERROR"] = "Incorrect syntax! Use: quests.player.reset [steamid64]",
				["QUESTS_INVALID_PLAYER_ID_INPUT"] = "Invalid input! Please enter a valid player ID.",
				["QUESTS_NOT_A_STEAM_ID"] = "The entered ID is not a SteamID. Please check and try again.",
				["QUESTS_PLAYER_PROGRESS_RESET"] = "The player's progress has been successfully reset!",
				["QUESTS_PLAYER_NOT_FOUND_BY_STEAMID"] = "Player with the specified Steam ID not found.",

			}, this);
		}

		#endregion

		#region Configuration

		private Configuration _config;

		private class Configuration
		{
			public class Settings
			{
				[JsonProperty("Max number of concurrent quests")]
				public int questCount = 3;

				[JsonProperty("Play sound effect upon task completion")]
				public bool SoundEffect = true;

				[JsonProperty("Effect")]
				public string Effect = "assets/prefabs/locks/keypad/effects/lock.code.lock.prefab";

				[JsonProperty("Clear player progress when wipe ?")]
				public bool useWipe = true;
				[JsonProperty("Clean up player permissions when wiping?")]
				public bool useWipePermission = true;

				[JsonProperty("Quests file name")]
				public string questListDataName = "Quest";

				[JsonProperty("Commands to open quest list with progress", ObjectCreationHandling = ObjectCreationHandling.Replace)]
				public string[] questListProgress = { "qlist" };


				[JsonProperty("Notify all players on task completion?")]
				public bool sandNotifyAllPlayer = false;

			}

			public class TextStyleSettings
			{
				[JsonProperty("Enable text style customization")]
				public bool useTextStyleCustomization = true;

				[JsonProperty("Primary text color hex (without #)")]
				public string primaryColorHex = "4286f4";

				[JsonProperty("Secondary text color hex (without #)")]
				public string secondaryColorHex = "42a1f5";

				[JsonProperty("Apply prefix to chat messages")]
				public bool useChatPrefix = true;

				[JsonProperty("Chat prefix text (rich text supported)")]
				public string chatPrefix = "<color=#42a1f5>[QUESTS]</color> ";

				[JsonProperty("Apply bold style to chat messages")]
				public bool useBold = false;

				[JsonProperty("Apply italic style to chat messages")]
				public bool useItalic = false;
			}

			[JsonProperty("General Settings")]
			public Settings settings = new Settings();

			[JsonProperty("Text style settings")]
			public TextStyleSettings textStyleSettings = new TextStyleSettings();


		}

		protected override void LoadConfig()
		{
			base.LoadConfig();
			try
			{
				_config = Config.ReadObject<Configuration>();
				if (_config == null)
				{
					throw new Exception();
				}

				SaveConfig();
			}
			catch
			{
				for (int i = 0; i < 3; i++)
				{
					PrintError("Configuration file is corrupt! Check your config file at https://jsonlint.com/");
				}

				LoadDefaultConfig();
			}

			ValidateConfig();
			SaveConfig();
		}

		private void ValidateConfig()
		{
		}

		protected override void SaveConfig()
		{
			Config.WriteObject(_config);
		}

		protected override void LoadDefaultConfig()
		{
			_config = new Configuration();
		}

		#endregion

		#region QuestData

		private class PlayerQuest
		{
			public long ParentQuestID;
			public QuestType ParentQuestType;

			public ulong UserID;

			public bool Finished;
			public int Count;

			public void AddCount(int amount = 1)
			{
				Count += amount;
				BasePlayer player = BasePlayer.FindByID(UserID);
				Quest parentQuest = Instance._questList[ParentQuestID];
				if (parentQuest.ActionCount <= Count)
				{
					Count = parentQuest.ActionCount;
					if (player != null && player.IsConnected)
					{
						if (Instance._config.settings.SoundEffect)
						{
							Instance.RunEffect(player, Instance._config.settings.Effect);
						}

						Instance.SendChat(player, "QUESTS_Finished_QUEST".GetAdaptedMessage(player.UserIDString, parentQuest.GetDisplayName(Instance.lang.GetLanguage(player.UserIDString))));

						if (Instance._config.settings.sandNotifyAllPlayer)
						{
							foreach (BasePlayer players in BasePlayer.activePlayerList)
							{
								Instance.SendChat(players, "QUESTS_Finished_QUEST_ALL".GetAdaptedMessage( players.UserIDString, player.displayName,
									parentQuest.GetDisplayName(Instance.lang.GetLanguage(player.UserIDString))));
							}
						}

						Interface.CallHook("OnQuestCompleted", player, parentQuest.GetDisplayName(Instance.lang.GetLanguage(player.UserIDString)));
						Instance._questStatistics.GatherTaskStatistics(TaskType.TaskExecution, ParentQuestID);
						Instance._questStatistics.GatherTaskStatistics(TaskType.Completed);
					}

					Finished = true;
				}

				if (Instance._openMiniQuestListPlayers.Contains(UserID))
					Instance.UIMiniQuestList(player);
			}
		}

		public enum TaskType
		{
			Completed,
			Taken,
			Declined,
			TaskExecution
		}

		private enum QuestType
		{
			IQPlagueSkill = 0,      // Learn or upgrade plague skills
			IQHeadReward = 1,       // Complete head-reward style objectives
			IQCases = 2,            // Open custom cases
			OreBonus = 3,           // Trigger ore-bonus style objectives
			XDChinookIvent = 4,     // Complete XD Chinook event objectives
			Gather = 5,             // Collect resources/items
			EntityKill = 6,         // Kill animals/NPCs/players or destroy entities like barrels
			Craft = 7,              // Craft items
			Research = 8,           // Research items
			Loot = 9,               // Loot containers/entities
			Grade = 10,             // Upgrade building blocks
			Swipe = 11,             // Swipe keycards/readers
			Deploy = 12,            // Deploy entities/items
			PurchaseFromNpc = 13,   // Buy items from NPC vending machines
			HackCrate = 14,         // Hack locked crates
			RecycleItem = 15,       // Recycle items
			Growseedlings = 16,     // Grow seedlings/plants
			RaidableBases = 17,     // Complete raidable base objectives
			Fishing = 18,           // Catch fish
			BossMonster = 19,       // Kill boss monsters
			HarborEvent = 20,       // Complete harbor event objectives
			SatelliteDishEvent = 21,// Complete satellite dish event objectives
			Sputnik = 22,           // Complete Sputnik event objectives
			AbandonedBases = 23,    // Complete abandoned base objectives
			Delivery = 24,          // Deliver required items
			IQDronePatrol = 25,     // Complete IQ drone patrol objectives
			GasStationEvent = 26,   // Complete gas station event objectives
			Triangulation = 27,     // Complete triangulation objectives
			FerryTerminalEvent = 28,// Complete ferry terminal event objectives
			Convoy = 29,            // Complete convoy event objectives
			Caravan = 30,           // Complete caravan event objectives
			IQDefenderSupply = 31   // Complete IQ defender supply objectives
		}

		private enum PrizeType
		{
			Item,
			BluePrint,
			CustomItem,
			Command
		}

		private class Quest
		{
			internal class Prize
			{
				public string PrizeName;
				public PrizeType PrizeType;
				public string ItemShortName;
				public int ItemAmount;
				public string CustomItemName;
				public ulong ItemSkinID;
				public string PrizeCommand;
				public string CommandImageUrl;
				public bool IsHidden;
			}

			public long QuestID;
			public string QuestDisplayName;
			public string QuestDisplayNameMultiLanguage;
			public string QuestDescription;
			public string QuestDescriptionMultiLanguage;
			public string QuestMissions;
			public string QuestMissionsMultiLanguage;

			public string QuestPermission;
			public QuestType QuestType;
			public string Target;
			public int ActionCount;
			public bool IsRepeatable;
			public bool IsMultiLanguage;
			public bool IsReturnItemsRequired;
			public int Cooldown;
			
			[JsonIgnore]
			public string[] Targets = Array.Empty<string>();
			public List<Prize> PrizeList = new List<Prize>();

			public string GetDisplayName(string language) => language == "ru" || IsMultiLanguage == false ? QuestDisplayName : QuestDisplayNameMultiLanguage;
			public string GetDescription(string language) => language == "ru" || IsMultiLanguage == false ? QuestDescription : QuestDescriptionMultiLanguage;
			public string GetMissions(string language) => language == "ru" || IsMultiLanguage == false ? QuestMissions : QuestMissionsMultiLanguage;
		}

		#endregion


		#region Hooks

		#region QuestHook

		#region Type Upgrade

		private object OnStructureUpgrade(BaseCombatEntity entity, BasePlayer player, BuildingGrade.Enum grade)
		{
			QuestProgress(player.userID, QuestType.Grade, ((int)grade).ToString());
			return null;
		}

		#endregion

		#region IQPlagueSkill

		private void StudySkill(BasePlayer player, string name)
		{
			QuestProgress(player.userID, QuestType.IQPlagueSkill, name);
		}

		#endregion

		#region HeadReward

		private void KillHead(BasePlayer player)
		{
			QuestProgress(player.userID, QuestType.IQHeadReward);
		}

		#endregion

		#region IqCase

		private void OnOpenedCase(BasePlayer player, string name)
		{
			QuestProgress(player.userID, QuestType.IQCases, name);
		}

		#endregion

		#region Chinook

		private void LootHack(BasePlayer player)
		{
			QuestProgress(player.userID, QuestType.XDChinookIvent);
		}

		#endregion

		#region Gather

		#region GatherFix

		private void GatherHooksSub()
		{
			foreach (string hook in _gatherHooks)
				Subscribe(hook);
		}

		private string[] _gatherHooks =
		{
			"OnCollectiblePickedup",
			"OnDispenserGathered",
			"OnDispenserBonusReceived",
		};

		
		
		#endregion

		private void OnDispenserGathered(ResourceDispenser dispenser, BasePlayer player, Item item)
		{
			if(player == null) return;
			QuestProgress(player.userID, QuestType.Gather, item.info.shortname, "", null, item.amount);
		}
		
		private void OnDispenserBonusReceived(ResourceDispenser dispenser, BasePlayer player, Item item) => OnDispenserGathered(dispenser, player, item);

		private void OnCollectiblePickedup(CollectibleEntity collectible, BasePlayer player, Item item)
		{
			if (player == null || item == null)
				return;
			
			QuestProgress(player.userID, QuestType.Gather, item.info.shortname, "", null, item.amount);
		}

		private void STCanReceiveYield(BasePlayer player, GrowableEntity entity, Item item)
		{
			if (player == null || item == null || item.info == null) return;
			QuestProgress(player.userID, QuestType.Gather, item.info.shortname, "", null, item.amount);
		}

		private void STCanReceiveYield(BasePlayer player, CollectibleEntity entity, ItemAmount ia)
		{
			if (player == null || ia == null || ia.itemDef == null) return;
			QuestProgress(player.userID, QuestType.Gather, ia.itemDef.shortname, "", null, (int)ia.amount);
		}



		#endregion

		#region Craft

		private void OnItemCraftFinished(ItemCraftTask task, Item item, ItemCrafter crafter)
		{
			QuestProgress(crafter.owner.userID, QuestType.Craft, task.blueprint.targetItem.shortname, "", null, item.amount);
		}

		#endregion

		#region Research

		private void OnTechTreeNodeUnlocked(Workbench workbench, TechTreeData.NodeInstance node, BasePlayer player)
		{
			QuestProgress(player.userID, QuestType.Research, node.itemDef.shortname);
		}

		private void OnItemResearched(ResearchTable table, int amountToConsume)
		{
			QuestProgress(table.LastLootedBy, QuestType.Research, table.GetTargetItem().info.shortname);
		}

		#endregion

		#region Deploy

		private void OnEntityBuilt(Planner plan, GameObject go)
		{
			if(plan == null) return;
			BasePlayer player = plan.GetOwnerPlayer();
			if (player == null || go == null || plan.GetItem() == null)
			{
				return;
			}
			BaseEntity ent = go.ToBaseEntity();
			if (ent == null || ent.skinID == 11543256361)
			{
				return;
			}
			
			QuestProgress(player.userID, QuestType.Deploy, plan.GetItem().info.shortname);
		}

		#endregion
		
		#region Loot

		#region OnLootEntity

		private HashSet<ulong> Looted = new();
		
		private void OnEntityDestroy(BaseEntity entity)
		{
			if (entity == null) return;
			ulong net = entity.net?.ID.Value ?? 0;
			if (Looted.Contains(net))
				Looted.Remove(net);
		}
		private void OnLootEntity(BasePlayer player, BaseEntity entity)
		{
			if (entity == null || player == null)
				return;
			ulong netId = entity.net?.ID.Value ?? 0;
			if (!Looted.Add(netId))
				return;


			switch (entity)
			{
				case LootContainer lootContainer:
					if (lootContainer.inventory != null)
						QuestProgress(player.userID, QuestType.Loot, "", "", lootContainer.inventory.itemList);
					break;
				
				case LootableCorpse lootableCorpse:
					if(lootableCorpse.playerSteamID.IsSteamId())
						return;

					if (lootableCorpse.containers != null)
					{
						foreach (ItemContainer container in lootableCorpse.containers)
							if (container != null)
								QuestProgress(player.userID, QuestType.Loot, "", "", container.itemList);
					}
					break;
				
				case DroppedItemContainer droppedItemContainer:
					if(droppedItemContainer.prefabID != 1519640547 || droppedItemContainer.playerSteamID.IsSteamId())
						return;

					if (droppedItemContainer.inventory != null)
						QuestProgress(player.userID, QuestType.Loot, "", "", droppedItemContainer.inventory.itemList);
					break;
			}
		}
		
		private void OnContainerDropItems(ItemContainer container)
		{
			if (container == null || container.entityOwner == null)
				return;

			string prefabName = container.entityOwner.ShortPrefabName;
			if (prefabName == null || (!prefabName.Contains("barrel") && !prefabName.Contains("roadsign")))
				return;

			if (container.entityOwner is LootContainer lootContainer)
			{
				ulong netId = lootContainer.net?.ID.Value ?? 0;
				if (!Looted.Add(netId))
					return;

				if (lootContainer.lastAttacker is BasePlayer player)
				{
					QuestProgress(player.userID, QuestType.Loot, "", "", lootContainer.inventory.itemList);
				}
			}
		}

		#endregion

		#endregion

		#region Swipe

		private void OnCardSwipe(CardReader cardReader, Keycard card, BasePlayer player)
		{
			if (card == null || cardReader == null || player == null) return;
			if (!cardReader.HasFlag(BaseEntity.Flags.On) && card.accessLevel == cardReader.accessLevel)
				QuestProgress(player.userID, QuestType.Swipe, card.accessLevel.ToString());
		}

		#endregion

		#region Section
		
		private void OnPlayerDeath(BasePlayer player, HitInfo info)
		{
			if (player == null || info == null || !player.userID.IsSteamId())
				return;
			BasePlayer attacker = info.InitiatorPlayer;
			if (attacker == null)
				return;

			if (IsTeammate(player.userID.Get(), attacker.userID.Get()) || player.userID == attacker.userID)
				return;

			QuestProgress(attacker.userID, QuestType.EntityKill, "player");
		}
		
		private Dictionary<NetworkableId, ulong> heliCashed = new();
		private void OnPatrolHelicopterKill(PatrolHelicopter entity, HitInfo info)
		{
			if (entity == null || info == null || info.InitiatorPlayer == null)
				return;

			BasePlayer player = info.InitiatorPlayer;
			if (player.userID.IsSteamId())
			{
				heliCashed[entity.net.ID] = player.userID;
			}
		}
        
		private void OnEntityKill(PatrolHelicopter entity)
		{
			if (entity == null || entity.net == null)
				return;

			if (heliCashed.TryGetValue(entity.net.ID, out ulong playerId))
			{
				QuestProgress(playerId, QuestType.EntityKill, entity.ShortPrefabName.ToLowerInvariant());
				heliCashed.Remove(entity.net.ID);
			}
			else
			{
				if (entity.myAI != null && entity.myAI._targetList is { Count: > 0 } targetList)
				{
					BasePlayer player = targetList[^1].ply;

					if (player != null && player.userID.IsSteamId())
					{
						QuestProgress(player.userID, QuestType.EntityKill, entity.ShortPrefabName.ToLowerInvariant());
					}
				}
			}
		}
		
		private static readonly HashSet<string> ExcludedEntityDeathTargets = new(StringComparer.OrdinalIgnoreCase)
		{
			"corpse", "servergibs", "player", "rug.bear.deployed"
		};

		private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
		{
			if (entity == null || info == null || entity.GetComponent<PatrolHelicopter>() != null)
				return;

			BasePlayer player = info.InitiatorPlayer;
			if (player == null || player.IsNpc || entity.ToPlayer() == player)
				return;

			string name = entity.ShortPrefabName.ToLowerInvariant();
			if (ExcludedEntityDeathTargets.Contains(name))
				return;

			if (name == "testridablehorse")
				name = "horse";

			QuestProgress(player.userID, QuestType.EntityKill, name);
		}


		#endregion

		#region Section
		

		void OnNpcGiveSoldItem(NPCVendingMachine machine, Item soldItem, BasePlayer buyer)
		{
			QuestProgress(buyer.userID, QuestType.PurchaseFromNpc, soldItem.info.shortname, "", null, soldItem.amount);
		}

		#endregion

		#region Section

		private void OnCrateHack(HackableLockedCrate crate)
		{
			if (crate.originalHackerPlayerId.IsSteamId())
			{
				QuestProgress(crate.originalHackerPlayerId, QuestType.HackCrate);
			}
		}

		#endregion

		#region Section

		private readonly Dictionary<ulong, BasePlayer> _recyclePlayer = new();

		private void OnRecyclerToggle(Recycler recycler, BasePlayer player)
		{
			if (!recycler.IsOn())
			{
				if (!_recyclePlayer.TryAdd(recycler.net.ID.Value, player))
				{
					_recyclePlayer.Remove(recycler.net.ID.Value);
					_recyclePlayer.Add(recycler.net.ID.Value, player);
				}
			}
			else if (_recyclePlayer.ContainsKey(recycler.net.ID.Value))
			{
				_recyclePlayer.Remove(recycler.net.ID.Value);
			}
		}
		
		private void OnItemRecycle(Item item, Recycler recycler)
		{
			BasePlayer value;
			if (_recyclePlayer.TryGetValue(recycler.net.ID.Value, out value))
			{
				int num2 = 1;
				if (item.amount > 1)
				{
					num2 = Mathf.CeilToInt(Mathf.Min(item.amount, item.info.stackable * 0.1f));
				}
				QuestProgress(value.userID, QuestType.RecycleItem, item.info.shortname, "", null, num2);
			}
		}

		#endregion

		#region Growseedlings
		 
		private void OnGrowableGathered(GrowableEntity plant, Item item, BasePlayer player)
		{
			QuestProgress(player.userID, QuestType.Growseedlings, item.info.shortname, "", null, item.amount);
		}

		#endregion

		#region Raidable Bases (Nivex)

		private void OnRaidableBaseCompleted(Vector3 location, int mode, bool allowPVP, string id, float spawnTime, float despawnTime, float loadingTime, ulong ownerId, BasePlayer owner,
			List<BasePlayer> raiders)
		{
			BasePlayer player = owner ? owner : (raiders?.Count != 0 ? raiders[0] : null);
			if (player != null)
			{
				QuestProgress(player.userID, QuestType.RaidableBases, mode.ToString(), "", null);
			}
		}

		#endregion

		#region Fishing

		private void OnFishCatch(Item fish, BaseFishingRod fishingRod, BasePlayer player)
		{
			if (player == null || fish == null)
				return;

			QuestProgress(player.userID, QuestType.Fishing, fish.info.shortname, "", null, fish.amount);
		}

		#endregion

		#region BossMonster

		private void OnBossKilled(ScientistNPC boss, BasePlayer attacker)
		{
			if (boss == null || attacker == null)
				return;

			QuestProgress(attacker.userID, QuestType.BossMonster, boss.displayName, "", null);
		}

		#endregion

		#region HarborEvent

		private void OnHarborEventWinner(ulong winnerId)
		{
			QuestProgress(winnerId, QuestType.HarborEvent);
		}

		#endregion

		#region SatelliteDishEvent

		private void OnSatDishEventWinner(ulong winnerId)
		{
			QuestProgress(winnerId, QuestType.SatelliteDishEvent);
		}

		#endregion

		#region Sputnik

		private void OnSputnikEventWin(ulong userID)
		{
			QuestProgress(userID, QuestType.Sputnik);
		}

		#endregion

		#region AbandonedBases

		private void OnAbandonedBaseEnded(Vector3 center, bool allowPVP, List<BasePlayer> intruders)
		{
			if (intruders.Count <= 0)
				return;

			foreach (BasePlayer player in intruders)
			{
				QuestProgress(player.userID, QuestType.AbandonedBases);
			}
		}

		#endregion

		#region IQDronePatrol

		private void OnDroneKilled(BasePlayer player, Drone drone, string KeyDrone)
		{
			if (player == null || drone == null)
				return;

			QuestProgress(player.userID, QuestType.IQDronePatrol, KeyDrone, "", null);
		}

		#endregion

		#region IQDefenderSupply

		private void OnLootedDefenderSupply(BasePlayer player, int levelDropInt)
		{
			if (player == null)
				return;
			
			QuestProgress(player.userID, QuestType.IQDefenderSupply, levelDropInt.ToString(), "", null);
		}

		#endregion

		#region GasStationEvent

		private void OnGasStationEventWinner(ulong userID)
		{
			QuestProgress(userID, QuestType.GasStationEvent);
		}

		#endregion

		#region Triangulation 

		private void OnTriangulationWinner(ulong userID)
		{
			QuestProgress(userID, QuestType.Triangulation);
		}

		#endregion

		#region FerryTerminalEvent

		private void OnFerryTerminalEventWinner(ulong userID)
		{
			QuestProgress(userID, QuestType.FerryTerminalEvent);
		}

		#endregion

		#region Convoy

		private void OnConvoyEventWin(ulong userID)
		{
			QuestProgress(userID, QuestType.Convoy);
		}

		#endregion

		#region Caravan
		
		private void OnCaravanEventWin(ulong userID)
		{
			QuestProgress(userID, QuestType.Caravan);
		}

		#endregion

		#endregion


		private void OnNewSave()
		{
			if (_config.settings.useWipe)
			{
				_playersInfo?.Clear();
				SaveData();
			}

			if (_config.settings.useWipePermission)
			{
				ClearPermission();
			}
		}


		private void Init()
		{
			Instance = this;
			LoadPlayerData();
			LoadQuestStatisticsData();
			LoadQuestData();
		}
		
		private void OnServerInitialized()
		{
			HashSet<string> chatCommands = new(StringComparer.OrdinalIgnoreCase);
			if (_config?.settings?.questListProgress != null)
			{
				foreach (string command in _config.settings.questListProgress)
				{
					if (!string.IsNullOrWhiteSpace(command))
						chatCommands.Add(command.Trim());
				}
			}

			foreach (string command in chatCommands)
			{
				if (command.Equals("quest", StringComparison.OrdinalIgnoreCase))
					continue;

				cmd.AddChatCommand(command, this, nameof(QuestAliasCommand));
			}

			GatherHooksSub();

			_imageUI = new ImageUI();
			_imageUI.DownloadImage();

			foreach (BasePlayer player in BasePlayer.activePlayerList)
				OnPlayerConnected(player);
		}

		private void CheckPlayerCooldowns(BasePlayer player)
		{
			PlayerData playerData;
			if (_playersInfo.TryGetValue(player.userID, out playerData))
			{
				List<long> questsToRemove = Pool.Get<List<long>>();

				foreach (KeyValuePair<long, double> cooldownForQuest in playerData.PlayerQuestCooldowns)
				{
					if (CurrentTime() >= cooldownForQuest.Value + 30f)
					{
						questsToRemove.Add(cooldownForQuest.Key);

						if (_questList.TryGetValue(cooldownForQuest.Key, out Quest quest))
						{
							string userId = player.UserIDString;
							SendChat(player, "QUESTS_REPEATABLE_QUEST_AVAILABLE_AGAIN".GetAdaptedMessage(userId, quest.GetDisplayName(lang.GetLanguage(userId))));
						}
					}
				}

				foreach (long questId in questsToRemove)
					playerData.PlayerQuestCooldowns.Remove(questId);
				
				Pool.FreeUnmanaged(ref questsToRemove);
			}
		}

		private void OnPlayerConnected(BasePlayer player)
		{
			ulong UserId = player.userID.Get();
			PlayerData playerData;
			if (!_playersInfo.TryGetValue(UserId, out playerData))
			{
				_playersInfo.Add(UserId, new PlayerData());
			}
			else
			{
				List<PlayerQuest> questsToRemove = new();

				foreach (PlayerQuest item in playerData.CurrentPlayerQuests)
				{
					KeyValuePair<long, Quest>? currentQuest = null;

					foreach (KeyValuePair<long, Quest> pair in _questList)
					{
						if (pair.Key == item.ParentQuestID && pair.Value.QuestType == item.ParentQuestType)
						{
							currentQuest = pair;
							break;
						}
					}

					if (currentQuest?.Value == null)
					{
						questsToRemove.Add(item);
					}
				}

				NextTick(() =>
				{
					foreach (PlayerQuest questToRemove in questsToRemove)
					{
						playerData.CurrentPlayerQuests.Remove(questToRemove);
					}

				});
			}
		}

		private void OnServerSave()
		{
			SaveData();
		}

		private void OnPlayerDisconnected(BasePlayer player)
		{
			ulong UserId = player.userID.Get();

			_openMiniQuestListPlayers.Remove(UserId);
		}

		private void OnServerShutdown() => Unload();

		private void Unload()
		{
			if (IsObjectNull(Instance))
				return;

			if (_imageUI != null)
			{
				_imageUI.UnloadImages();
				_imageUI = null;
			}

			Instance = null;
			SaveData();
			
			ClearPlayersData();
		}

		



		#endregion

		#region HelpMetods

		#region HelpUnload

		private void UnloadWithMessage(string message)
		{
			NextTick(() =>
			{
				PrintError(message);
				Interface.Oxide.UnloadPlugin(Name);
			});
		}


		private void ClearPlayersData()
		{
			foreach (BasePlayer p in BasePlayer.activePlayerList)
			{
				CuiHelper.DestroyUi(p, MINI_QUEST_LIST);
				CuiHelper.DestroyUi(p, LAYERS);
			}
		}

		#endregion

		private static bool IsObjectNull(object obj) => ReferenceEquals(obj, null);

		private static string GetFileNameWithoutExtension(string filePath)
		{
			int lastDirectorySeparatorIndex = filePath.LastIndexOfAny(new[] { '\\', '/' });
			int lastDotIndex = filePath.LastIndexOf('.');

			if (lastDotIndex > lastDirectorySeparatorIndex)
			{
				return filePath.Substring(lastDirectorySeparatorIndex + 1, lastDotIndex - lastDirectorySeparatorIndex - 1);
			}

			return filePath.Substring(lastDirectorySeparatorIndex + 1);
		}

		private void RunEffect(BasePlayer player, string path)
		{
			Effect effect = new Effect();
			Transform transform = player.transform;
			effect.Init(Effect.Type.Generic, transform.position, transform.forward);
			effect.pooledString = path;
			EffectNetwork.Send(effect, player.net.connection);
		}
		
		private void ClearPermission()
		{
			string[] allPermissions = permission.GetPermissions();
			const string permissionPrefix = "Quests.";

			foreach (string perm in allPermissions)
			{
				if (perm.Equals($"{permissionPrefix}default", StringComparison.OrdinalIgnoreCase))
					continue;

				if (perm.StartsWith(permissionPrefix, StringComparison.OrdinalIgnoreCase))
				{
					string[] usersWithPermission = permission.GetPermissionUsers(perm);

					foreach (string userEntry in usersWithPermission)
					{
						string steamId = ExtractSteamId(userEntry);
						permission.RevokeUserPermission(steamId, perm);
					}
				}
			}
		}

		private string ExtractSteamId(string userEntry)
		{
			int separatorIndex = userEntry.IndexOf('(');
			return separatorIndex > 0 ? userEntry[..separatorIndex] : userEntry;
		}

		private static class TimeHelper
		{
			public static string FormatTime(TimeSpan time, int maxSubstr = 5, string language = "ru")
			{
				return language == "ru" ? FormatTimeRussian(time, maxSubstr) : FormatTimeDefault(time);
			}

			private static string FormatTimeRussian(TimeSpan time, int maxSubstr)
			{
				List<string> substrings = new();

				if (time.Days != 0 && substrings.Count < maxSubstr)
				{
					substrings.Add(Format(time.Days, ""));
				}

				if (time.Hours != 0 && substrings.Count < maxSubstr)
				{
					substrings.Add(Format(time.Hours, ""));
				}

				if (time.Minutes != 0 && substrings.Count < maxSubstr)
				{
					substrings.Add(Format(time.Minutes, ""));
				}

				if (time.Days == 0 && time.Seconds != 0 && substrings.Count < maxSubstr)
				{
					substrings.Add(Format(time.Seconds, ""));
				}

				if (substrings.Count == 0)
				{
					substrings.Add("0");
				}

				return string.Join(" ", substrings);
			}

			private static string FormatTimeDefault(TimeSpan time)
			{
				List<string> parts = new List<string>();

				if (time.Days > 0)
				{
					parts.Add($"{time.Days} day{(time.Days == 1 ? string.Empty : "s")}");
				}

				if (time.Hours > 0)
				{
					parts.Add($"{time.Hours} hour{(time.Hours == 1 ? string.Empty : "s")}");
				}

				if (time.Minutes > 0)
				{
					parts.Add($"{time.Minutes} minute{(time.Minutes == 1 ? string.Empty : "s")}");
				}

				if (time.Seconds > 0)
				{
					parts.Add($"{time.Seconds} second{(time.Seconds == 1 ? string.Empty : "s")}");
				}

				if (parts.Count == 0)
				{
					parts.Add("0 seconds");
				}

				return string.Join(", ", parts);
			}

			private static string Format(int units, string form)
			{
				return $"{units}{form}";
			}
		}


		private void LoadPlayerData()
		{
			try
			{
				_playersInfo = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerData>>(PlayerDataFileName) ?? new Dictionary<ulong, PlayerData>();
			}
			catch (Exception ex)
			{
				PrintWarning($"Failed to read player data file. A new in-memory dataset will be used. Error: {ex.Message}");
				_playersInfo = new Dictionary<ulong, PlayerData>();
			}
		}

		private void SaveData()
		{
			Interface.Oxide.DataFileSystem.WriteObject(PlayerDataFileName, _playersInfo);
			Interface.Oxide.DataFileSystem.WriteObject(StatisticsDataFileName, _questStatistics);
		}

		private void LoadQuestStatisticsData()
		{
			try
			{
				_questStatistics = Interface.Oxide.DataFileSystem.ReadObject<QuestStatistics>(StatisticsDataFileName) ?? new QuestStatistics();
			}
			catch (Exception ex)
			{
				PrintWarning($"Failed to read statistics data file. A new in-memory statistics dataset will be used. Error: {ex.Message}");
				_questStatistics = new QuestStatistics();
			}
		}

		private string GetQuestDataFilePath() => $"{Name}/{_config.settings.questListDataName}";

		private void LoadQuestData()
		{
			string questDataPath = GetQuestDataFilePath();
			if (!Interface.Oxide.DataFileSystem.ExistsDatafile(questDataPath))
			{
				List<Quest> defaults = BuildDefaultQuestExamples();
				Interface.Oxide.DataFileSystem.WriteObject(questDataPath, defaults);
				Puts($"Quest data file was missing and has been generated with {defaults.Count} example quests: data/{questDataPath}.json");
			}

			List<Quest> loadedQuests;
			try
			{
				loadedQuests = Interface.Oxide.DataFileSystem.ReadObject<List<Quest>>(questDataPath) ?? new List<Quest>();
			}
			catch (Exception ex)
			{
				PrintError($"Failed to parse quest data file at data/{questDataPath}.json. File was not modified. Error: {ex.Message}");
				return;
			}

			_questList.Clear();
			foreach (Quest quest in loadedQuests)
			{
				if (!TryValidateQuest(quest, out string reason))
				{
					PrintWarning($"Skipping invalid quest entry (ID: {quest?.QuestID.ToString() ?? "null"}): {reason}");
					continue;
				}

				quest.Targets = (quest.Target ?? string.Empty)
					.Split(',')
					.Select(x => x.Trim().ToLowerInvariant())
					.Where(x => !string.IsNullOrEmpty(x))
					.ToArray();
				if (quest.Targets.Length == 1)
					quest.Target = quest.Targets[0];

				_questList[quest.QuestID] = quest;
				if (!string.IsNullOrEmpty(quest.QuestPermission))
					permission.RegisterPermission($"{Name}.{quest.QuestPermission}", this);
			}
		}

		private static bool TryValidateQuest(Quest quest, out string reason)
		{
			if (quest == null)
			{
				reason = "Quest entry is null";
				return false;
			}
			if (quest.QuestID <= 0)
			{
				reason = "QuestID must be greater than 0";
				return false;
			}
			if (string.IsNullOrWhiteSpace(quest.QuestDisplayName))
			{
				reason = "QuestDisplayName is required";
				return false;
			}
			if (quest.ActionCount <= 0)
			{
				reason = "ActionCount must be greater than 0";
				return false;
			}
			if (quest.PrizeList == null)
				quest.PrizeList = new List<Quest.Prize>();
			reason = null;
			return true;
		}

		private List<Quest> BuildDefaultQuestExamples()
		{
			List<Quest> defaults = new List<Quest>();
			long questId = 1;
			foreach (QuestType questType in Enum.GetValues(typeof(QuestType)))
			{
				defaults.Add(new Quest
				{
					QuestID = questId++,
					QuestDisplayName = $"{questType} Example",
					QuestDescription = $"Example quest for {questType}.",
					QuestMissions = $"Complete {questType} objective",
					QuestPermission = "default",
					QuestType = questType,
					Target = "0",
					ActionCount = 1,
					IsRepeatable = true,
					Cooldown = 0,
					PrizeList = new List<Quest.Prize>
					{
						new Quest.Prize
						{
							PrizeType = PrizeType.Item,
							PrizeName = "Scrap",
							ItemShortName = "scrap",
							ItemAmount = 10,
							IsHidden = false
						}
					}
				});
			}
			return defaults;
		}

		private class QuestStatistics
		{
			public int CompletedTasks;
			public int TakenTasks;
			public int DeclinedTasks;
			public Dictionary<long, int> ExecutedByQuest = new Dictionary<long, int>();

			public void GatherTaskStatistics(TaskType taskType, long questId = 0)
			{
				switch (taskType)
				{
					case TaskType.Completed:
						CompletedTasks++;
						break;
					case TaskType.Taken:
						TakenTasks++;
						break;
					case TaskType.Declined:
						DeclinedTasks++;
						break;
					case TaskType.TaskExecution:
						if (questId > 0)
						{
							if (!ExecutedByQuest.ContainsKey(questId))
								ExecutedByQuest[questId] = 0;
							ExecutedByQuest[questId]++;
						}
						break;
				}
			}
		}

		private static double CurrentTime()
		{
			return Facepunch.Math.Epoch.Current;
		}


		private float DegreeToRadian(float angle)
		{
			return (float)(Math.PI * angle / 180.0f);
		}
		private float RadianToDegree(float radians)
		{
			return (float)(radians * 180.0f / Math.PI);
		}

		private void Log(string msg, string file)
		{
			LogToFile(file, $"[{DateTime.Now}] {msg}", this);
		}

		#endregion

		#region NewUi

		private List<ulong> _openMiniQuestListPlayers = new();
		private const string MINI_QUEST_LIST = "Mini_QuestList";
		private const string LAYERS = "UI_QuestMain";
		private const string LAYER_MAIN_BACKGROUND = "UI_QuestMainBackground";
		private const string QUESTS_CATEGORY_MAIN = "QUESTS_CATEGORY_MAIN";

		#region MainUI

		private void MainUi(BasePlayer player)
		{
			CuiElementContainer container = new CuiElementContainer
			{
				{
					new CuiPanel
					{
						CursorEnabled = true,
						Image = { Color = "1 1 1 0" },
						RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
					},
					"OverlayNonScaled",
					LAYERS
				},


				new CuiElement
				{
					Name = LAYER_MAIN_BACKGROUND,
					Parent = LAYERS,
					Components =
					{
						new CuiRawImageComponent { Color = "1 1 1 1", Png = _imageUI.GetImage("1") },
						new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
					}
				},

				new CuiElement
				{
					Name = "CloseUIImage",
					Parent = LAYER_MAIN_BACKGROUND,
					Components =
					{
						new CuiRawImageComponent { Color = "1 1 1 1", Png = _imageUI.GetImage("2") },
						new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "96.039 87.558", OffsetMax = "135.315 114.647" }
					}
				},

				{
					new CuiButton
					{
						Button = { Color = "1 1 1 0", Command = "CloseMainUI" },
						Text = { Text = "", Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0 0 0 1" },
						RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "96.039 87.558", OffsetMax = "135.315 114.647" }
					},
					LAYER_MAIN_BACKGROUND,
					"BtnCloseUI"
				},

				{
					new CuiLabel
					{
						RectTransform = { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = "96.227 191.4", OffsetMax = "208.973 211.399" },
						Text =
						{
							Text = "QUESTS_UI_TASKLIST".GetAdaptedMessage(player.UserIDString), Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleLeft,
							Color = "0.7169812 0.7169812 0.7169812 1"
						}
					},
					LAYER_MAIN_BACKGROUND,
					"LabelQuestList"
				},

				{
					new CuiLabel
					{
						RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-269.184 -102.227", OffsetMax = "-197.242 -72.373" },
						Text =
						{
							Text = "QUESTS_UI_Awards".GetAdaptedMessage(player.UserIDString), Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1"
						}
					},
					LAYER_MAIN_BACKGROUND,
					"PrizeTitle"
				},

				{
					new CuiLabel
					{
						RectTransform = { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = "250.187 191.399", OffsetMax = "350.187 211.401" },
						Text =
						{
							Text = "QUESTS_UI_TASKCount".GetAdaptedMessage(player.UserIDString, 0), Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleRight,
							Color = "1 1 1 1"
						}
					},
					LAYER_MAIN_BACKGROUND,
					"LabelQuestCount", "LabelQuestCount"
				}
			};

			CuiHelper.DestroyUi(player, "UI_QuestMain");
			CuiHelper.AddUi(player, container);
			Category(player, UICategory.Available);
			QuestListUI(player, UICategory.Available);
			QuestInfo(player, 0, UICategory.Available);
		}

		private void UpdateTasksCount(BasePlayer player, int count)
		{
			CuiElementContainer container = new CuiElementContainer
			{
				{
					new CuiLabel
					{
						RectTransform = { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = "250.187 191.399", OffsetMax = "350.187 211.401" },
						Text =
						{
							Text = "QUESTS_UI_TASKCount".GetAdaptedMessage(player.UserIDString, count), Font = "robotocondensed-regular.ttf", FontSize = 14,
							Align = TextAnchor.MiddleRight, Color = "1 1 1 1"
						}
					},
					LAYER_MAIN_BACKGROUND,
					"LabelQuestCount", "LabelQuestCount"
				}
			};
			CuiHelper.AddUi(player, container);
		}

		#endregion

		#region Category

		private enum UICategory
		{
			Available,
			Taken,
		}

		private List<Quest> GetQuestsByCategory(UICategory category, ulong playerId)
		{
			List<Quest> result = new List<Quest>();

			PlayerData playerData = _playersInfo[playerId];
			if (playerData == null)
			{
				return result;
			}

			switch (category)
			{
				case UICategory.Available:
					foreach (Quest quest in _questList.Values)
					{
						if (!string.IsNullOrEmpty(quest.QuestPermission) && !permission.UserHasPermission(playerId.ToString(), $"{Name}." + quest.QuestPermission)) continue;

						bool isQuestAlreadyTaken = playerData.CurrentPlayerQuests.Exists(pq => pq.ParentQuestID == quest.QuestID);
						bool isQuestCd = playerData.PlayerQuestCooldowns.ContainsKey(quest.QuestID);
						bool isQuestAlreadyFinish = playerData.CompletedQuestIds.Contains(quest.QuestID);

						if (!isQuestAlreadyTaken && !isQuestAlreadyFinish && !isQuestCd)
						{
							result.Add(quest);
						}
					}

					break;

				case UICategory.Taken:
					foreach (PlayerQuest playerQuest in playerData.CurrentPlayerQuests)
					{
						Quest value;
						if (_questList.TryGetValue(playerQuest.ParentQuestID, out value))
						{
							result.Add(value);
						}
					}

					foreach (long questId in playerData.PlayerQuestCooldowns.Keys)
					{
						Quest value;
						if (_questList.TryGetValue(questId, out value))
						{
							result.Add(value);
						}
					}

					break;
			}

			return result;
		}

		private void Category(BasePlayer player, UICategory category)
		{
			string color1 = category == UICategory.Available ? "0.4509804 0.5529412 0.2705882 0.8392157" : "0.6431373 0.6509804 0.654902 0.4";
			string color2 = category == UICategory.Taken ? "0.4509804 0.5529412 0.2705882 0.8392157" : "0.6431373 0.6509804 0.654902 0.4";
			string img = _imageUI.GetImage("16");
			CuiElementContainer container = new CuiElementContainer();

			container.Add(new CuiPanel
			{
				CursorEnabled = false,
				Image = { Color = "1 1 1 0" },
				RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-541.65 234.6", OffsetMax = "-284.33 337.6" }
			}, LAYER_MAIN_BACKGROUND, QUESTS_CATEGORY_MAIN, QUESTS_CATEGORY_MAIN);

			container.Add(new CuiElement
			{
				Name = "QUESTS_CATEGORY_SPRITE",
				Parent = QUESTS_CATEGORY_MAIN,
				Components =
				{
					new CuiRawImageComponent { Color = "0.7529412 0.5137255 0.04705882 1", Sprite = "assets/icons/Favourite_active.png", Material = "assets/icons/iconmaterial.mat", },
					new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "1.3 -22.5", OffsetMax = "21.3 -2.5" }
				}
			});

			container.Add(new CuiLabel
			{
				RectTransform = { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-103.66 -25", OffsetMax = "125.915 0" },
				Text =
				{
					Text = "QUESTS_UI_CATEGORY".GetAdaptedMessage(player.UserIDString), Font = "robotocondensed-bold.ttf", FontSize = 12, Align = TextAnchor.MiddleLeft,
					Color = "0.7169812 0.7169812 0.7169812 1"
				}
			}, QUESTS_CATEGORY_MAIN, "QUESTS_CATEGORY_TITLE");


			container.Add(new CuiElement
			{
				Name = "QUESTS_CATEGORY_BTN_1",
				Parent = QUESTS_CATEGORY_MAIN,
				Components =
				{
					new CuiRawImageComponent { Color = color1, Png = img },
					new CuiRectTransformComponent { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = "0 4.8", OffsetMax = "150 22.8" }
				}
			});

			container.Add(new CuiButton
			{
				RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
				Button = { Color = "0 0 0 0", Command = category == UICategory.Available ? "" : $"UI_Handler category {UICategory.Available.ToString()}" },
				Text =
				{
					Text = "QUESTS_UI_CATEGORY_ONE".GetAdaptedMessage(player.UserIDString), Font = "robotocondensed-regular.ttf", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1"
				}
			}, "QUESTS_CATEGORY_BTN_1");

			container.Add(new CuiElement
			{
				Name = "QUESTS_CATEGORY_BTN_2",
				Parent = QUESTS_CATEGORY_MAIN,
				Components =
				{
					new CuiRawImageComponent { Color = color2, Png = img },
					new CuiRectTransformComponent { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = "0 -17.2", OffsetMax = "150 0.8" }
				}
			});

			container.Add(new CuiButton
			{
				RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
				Button = { Color = "0 0 0 0", Command = category == UICategory.Taken ? "" : $"UI_Handler category {UICategory.Taken.ToString()}" },
				Text =
				{
					Text = "QUESTS_UI_CATEGORY_TWO".GetAdaptedMessage(player.UserIDString), Font = "robotocondensed-regular.ttf", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1"
				}
			}, "QUESTS_CATEGORY_BTN_2");

			CuiHelper.DestroyUi(player, QUESTS_CATEGORY_MAIN);
			CuiHelper.AddUi(player, container);
		}

		#endregion

		#region QuestList

		private void AddPageButton(string direction, string parentId, string command, CuiElementContainer container)
		{
			string buttonName = direction == "UP" ? "UPBTN" : "DOWNBTN";
			string imageName = direction == "UP" ? "3" : "4";
			string offsetMin = direction == "UP" ? "182.89 87.565" : "139.598 87.568";
			string offsetMax = direction == "UP" ? "221.51 114.635" : "178.326 114.632";

			container.Add(new CuiElement
			{
				Parent = parentId,
				Name = buttonName,
				Components =
				{
					new CuiRawImageComponent { Png = _imageUI.GetImage(imageName), Color = "1 1 1 1" },
					new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = offsetMin, OffsetMax = offsetMax }
				}
			});

			container.Add(new CuiButton
			{
				RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
				Button = { Color = "0 0 0 0", Command = command },
				Text = { Text = "" }
			}, buttonName);
		}

		private void QuestListUI(BasePlayer player, UICategory category, int page = 0)
		{
			List<PlayerQuest> playerQuests = _playersInfo[player.userID].CurrentPlayerQuests;
			if (playerQuests == null)
			{
				return;
			}

			int y = 0;
			CuiElementContainer container = new CuiElementContainer
			{
				{
					new CuiPanel
					{
						Image = { Color = "0 0 0 0" },
						RectTransform = { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = "96.23 -234.241", OffsetMax = "347.79 181.441" }
					},
					LAYER_MAIN_BACKGROUND,
					"QuestListPanel", "QuestListPanel"
				}
			};
			List<Quest> ql = GetQuestsByCategory(category, player.userID);
			if (page == 0)
				UpdateTasksCount(player, ql.Count);


			#region PageSettings

			if (page != 0)
			{
				AddPageButton("UP", LAYER_MAIN_BACKGROUND, $"UI_Handler page {page - 1} {category.ToString()}", container);
			}

			if (page + 1 < (int)Math.Ceiling((double)ql.Count / 6))
			{
				AddPageButton("DOWN", LAYER_MAIN_BACKGROUND, $"UI_Handler page {page + 1} {category.ToString()}", container);
			}

			#endregion

			if (ql.Count <= 0)
			{
				container.Add(new CuiLabel
				{
					RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
					Text =
					{
						Text = "QUESTS_UI_TASKS_LIST_EMPTY".GetAdaptedMessage(player.UserIDString), Font = "robotocondensed-bold.ttf", FontSize = 15, Align = TextAnchor.MiddleCenter,
						Color = "1 1 1 1"
					}
				}, "QuestListPanel");
			}

			foreach (Quest item in ql.Page(page, 6))
			{
				container.Add(new CuiElement
				{
					Name = "Quest",
					Parent = "QuestListPanel",
					Components =
					{
						new CuiRawImageComponent { Color = $"1 1 1 1", Png = _imageUI.GetImage("5") },
						new CuiRectTransformComponent
							{ AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = $"-125.78 {-67.933 - (y * 69.413)}", OffsetMax = $"125.78 {-1.06 - (y * 69.413)}" }
					}
				});
				container.Add(new CuiLabel
				{
					RectTransform = { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-109.661 -33", OffsetMax = "113.14 -12.085" },
					Text =
					{
						Text = item.GetDisplayName(lang.GetLanguage(player.UserIDString)), Font = "robotocondensed-bold.ttf", FontSize = 13, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1"
					}
				}, "Quest", "QuestName");
				if (category == UICategory.Taken)
				{
					PlayerQuest foundQuest = playerQuests.Find(quest => quest.ParentQuestID == item.QuestID);
					if (foundQuest != null)
					{
						string img, txt;
						if (foundQuest.Finished)
						{
							img = "15";
							txt = "QUESTS_UI_CHIPperformed".GetAdaptedMessage(player.UserIDString);
						}
						else
						{
							img = "14";
							txt = "QUESTS_UI_CHIPInProgress".GetAdaptedMessage(player.UserIDString);
						}

						container.Add(new CuiElement
						{
							Name = "QuestBar",
							Parent = "Quest",
							Components =
							{
								new CuiRawImageComponent { Color = "1 1 1 1", Png = _imageUI.GetImage(img) },
								new CuiRectTransformComponent { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = "17.19 -16.717", OffsetMax = "97.902 -2.411" }
							}
						});
						container.Add(new CuiLabel
						{
							RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-34.924 -7.153", OffsetMax = "40.356 7.153" },
							Text = { Text = txt, Font = "robotocondensed-bold.ttf", FontSize = 10, Align = TextAnchor.UpperCenter, Color = "1 1 1 1" }
						}, "QuestBar", "BarLabel");
					}
				}


				container.Add(new CuiButton
				{
					RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMax = "0 0" },
					Button = { Color = "0 0 0 0", Command = $"UI_Handler questinfo {item.QuestID} {category.ToString()} {page}" },
					Text = { Text = "" }
				}, $"Quest");
				y++;
			}

			CuiHelper.DestroyUi(player, "DOWNBTN");
			CuiHelper.DestroyUi(player, "UPBTN");
			CuiHelper.AddUi(player, container);
		}

		#endregion

		#region QuestInfo

		private void QuestInfo(BasePlayer player, long questID, UICategory category, int page = 0)
		{
			List<PlayerQuest> playerQuests = _playersInfo[player.userID].CurrentPlayerQuests;
			if (playerQuests == null)
			{
				return;
			}

			PlayerQuest foundQuest = playerQuests.Find(quest => quest.ParentQuestID == questID);
			Quest quests = null;
			Quest value;
			if (_questList.TryGetValue(questID, out value))
				quests = value;
			string playerLaunguage = lang.GetLanguage(player.UserIDString);

			CuiElementContainer container = new CuiElementContainer();

			container.Add(new CuiPanel
			{
				Image = { Color = "1 1 1 0" },
				RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-280.488 -234.241", OffsetMax = "564.144 212.279" }
			}, LAYER_MAIN_BACKGROUND, "QuestInfoPanel", "QuestInfoPanel");

			if (questID == 0 || quests == null)
			{
				container.Add(new CuiLabel
				{
					RectTransform = { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-398.895 -289.293", OffsetMax = "106.815 -76.2" },
					Text =
					{
						Text = "QUESTS_UI_TASKS_INFO_EMPTY".GetAdaptedMessage(player.UserIDString), Font = "robotocondensed-regular.ttf", FontSize = 19, Align = TextAnchor.MiddleCenter,
						Color = "1 1 1 1"
					}
				}, "QuestInfoPanel");

				CuiHelper.AddUi(player, container);
				return;
			}


			container.Add(new CuiLabel
			{
				RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "23.704 -42.956", OffsetMax = "420.496 -16.044" },
				Text = { Text = quests.GetDisplayName(playerLaunguage), Font = "robotocondensed-bold.ttf", FontSize = 19, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" }
			}, "QuestInfoPanel", "QuestName");

			string userepeat = quests.IsRepeatable ? "QUESTS_UI_QUESTREPEATCAN".GetAdaptedMessage(player.UserIDString) : "QUESTS_UI_QUESTREPEATfForbidden".GetAdaptedMessage(player.UserIDString);
			string useCooldown = quests.Cooldown > 0
				? TimeHelper.FormatTime(TimeSpan.FromSeconds(quests.Cooldown), 5, playerLaunguage)
				: "QUESTS_UI_Missing".GetAdaptedMessage(player.UserIDString);
			string bring = quests.IsReturnItemsRequired ? "QUESTS_UI_QuestNecessary".GetAdaptedMessage(player.UserIDString) : "QUESTS_UI_QuestNotNecessary".GetAdaptedMessage(player.UserIDString);
			container.Add(new CuiLabel
			{
				RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "23.705 -54.066", OffsetMax = "420.495 -40.134" },
				Text =
				{
					Text = "QUESTS_UI_InfoRepeatInCD".GetAdaptedMessage(player.UserIDString, userepeat, useCooldown, bring), Font = "robotocondensed-regular.ttf", FontSize = 10,
					Align = TextAnchor.UpperLeft, Color = "0.9607844 0.5843138 0.1960784 1"
				}
			}, "QuestInfoPanel", "QuestInfo2");

			container.Add(new CuiLabel
			{
				RectTransform = { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-398.895 -289.293", OffsetMax = "106.815 -76.2" },
				Text = { Text = quests.GetDescription(playerLaunguage), Font = "robotocondensed-regular.ttf", FontSize = 16, Align = TextAnchor.UpperLeft, Color = "1 1 1 1" }
			}, "QuestInfoPanel", "QuestDescription");

			#region QuestButton

			string buttonText = "", imageID = "", command = "", checkBox = "10";
			double? cooldownForQuest = _playersInfo[player.userID].GetCooldownForQuest(questID);

			if (foundQuest == null)
			{
				if (cooldownForQuest.HasValue)
				{
					imageID = "6";
					command = $"UI_Handler coldown";
				}
				else
				{
					if (!quests.IsRepeatable && _playersInfo[player.userID].CompletedQuestIds.Contains(quests.QuestID))
					{
						buttonText = "QUESTS_UI_QuestBtnPerformed".GetAdaptedMessage(player.UserIDString);
						imageID = "6";
						command = $"UI_Handler get {questID} {category.ToString()} {page}";
					}
					else
					{
						buttonText = "QUESTS_UI_QuestBtnTake".GetAdaptedMessage(player.UserIDString);
						imageID = "7";
						command = $"UI_Handler get {questID} {category.ToString()} {page}";
					}
				}
			}
			else if (foundQuest.Finished)
			{
				buttonText = "QUESTS_UI_QuestBtnPass".GetAdaptedMessage(player.UserIDString);
				imageID = "7";
				command = $"UI_Handler finish {questID} {category.ToString()} {page}";
				checkBox = "11";
			}
			else if (foundQuest.ParentQuestType == QuestType.Delivery)
			{
				container.Add(new CuiElement
				{
					Name = LAYERS + "QuestButtonImageA",
					Parent = "QuestInfoPanel",
					Components =
					{
						new CuiRawImageComponent { Color = "1 1 1 1", Png = _imageUI.GetImage("7") },
						new CuiRectTransformComponent { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-416.142 -49.709", OffsetMax = "-306.058 -7.691" }
					}
				});
				
				container.Add(new CuiButton
				{
					Button = { Color = "0 0 0 0", Command = $"UI_Handler finish {questID} {category.ToString()} {page}" },
					Text = { Text = "QUESTS_UI_QuestBtnDelivery".GetAdaptedMessage(player.UserIDString), Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
					RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1"}
				}, LAYERS + "QuestButtonImageA", LAYERS + "ButtonQuestA", LAYERS + "ButtonQuestA");
				
				container.Add(new CuiElement
				{
					Name = LAYERS + "QuestButtonImageC",
					Parent = "QuestInfoPanel",
					Components =
					{
						new CuiRawImageComponent { Color = "1 1 1 1", Png = _imageUI.GetImage("6") },
						new CuiRectTransformComponent { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-296.142 -49.709", OffsetMax = "-186.058 -7.691" }
					}
				});
				
				container.Add(new CuiButton
				{
					Button = { Color = "0 0 0 0", Command = $"UI_Handler finish {questID} {category.ToString()} {page} true" },
					Text = { Text = "QUESTS_UI_QuestBtnRefuse".GetAdaptedMessage(player.UserIDString), Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
					RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1"}
				}, LAYERS + "QuestButtonImageC", LAYERS + "ButtonQuestC", LAYERS + "ButtonQuestC");
			}
			else
			{
				buttonText = "QUESTS_UI_QuestBtnRefuse".GetAdaptedMessage(player.UserIDString);
				imageID = "6";
				command = $"UI_Handler finish {questID} {category.ToString()} {page}";
			}

			if (foundQuest is not { ParentQuestType: QuestType.Delivery })
			{
				container.Add(new CuiElement
				{
					Name = LAYERS + "QuestButtonImage",
					Parent = "QuestInfoPanel",
					Components =
					{
						new CuiRawImageComponent { Color = "1 1 1 1", Png = _imageUI.GetImage(imageID) },
						new CuiRectTransformComponent { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-416.142 -49.709", OffsetMax = "-306.058 -7.691" }
					}
				});

				if (cooldownForQuest.HasValue)
				{
					container.Add(new CuiButton
					{
						Button = { Color = "0 0 0 0", Command = command },
						Text = { Color = "1 1 1 1" },
						RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-55.039 -21.01", OffsetMax = "55.041 21.009" }
					}, LAYERS + "QuestButtonImage", LAYERS + "ButtonQuest");
					
					container.Add(new CuiElement
					{
						Parent = LAYERS + "ButtonQuest",
						Update = false, 
						Components =
						{
							new CuiCountdownComponent { StartTime = (float)(cooldownForQuest.Value - CurrentTime()), TimerFormat = TimerFormat.HoursMinutesSeconds, DestroyIfDone = true,},
							new CuiTextComponent { Text = $"%TIME_LEFT%", Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter },
						}
					});
				}
				else
				{
					container.Add(new CuiButton
					{
						Button = { Color = "0 0 0 0", Command = command },
						Text = { Text = buttonText, Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
						RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-55.039 -21.01", OffsetMax = "55.041 21.009" }
					}, LAYERS + "QuestButtonImage", LAYERS + "ButtonQuest", LAYERS + "ButtonQuest");
				}
			}

			#endregion

			#region QuestCheckBox

			container.Add(new CuiElement
			{
				Name = "QuestCheckBox",
				Parent = "QuestInfoPanel",
				Components =
				{
					new CuiRawImageComponent { Color = "1 1 1 1", Png = _imageUI.GetImage("9") },
					new CuiRectTransformComponent { AnchorMin = "1 0", AnchorMax = "1 0", OffsetMin = "-279.228 1.334", OffsetMax = "-1.217 125.64" }
				}
			});

			if (foundQuest?.ParentQuestType != QuestType.Delivery)
			{
				container.Add(new CuiElement
				{
					Name = "CheckBoxImg",
					Parent = "QuestCheckBox",
					Components =
					{
						new CuiRawImageComponent { Color = "1 1 1 1", Png = _imageUI.GetImage(checkBox) },
						new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "20.729 -35.467", OffsetMax = "38.205 -18.005" }
					}
				});
			}
			

			container.Add(new CuiLabel
			{
				RectTransform = { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-91.326 -55.693", OffsetMax = "136.647 -16.904" },
				Text = { Text = quests.GetMissions(playerLaunguage), Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.UpperLeft, Color = "1 1 1 1" }
			}, "QuestCheckBox", "CheckBoxTxt");

			if (foundQuest != null && foundQuest.ParentQuestType != QuestType.Delivery)
			{
				double factor = 278.005 * foundQuest.Count / quests.ActionCount;
				container.Add(new CuiPanel
				{
					CursorEnabled = false,
					Image = { Color = "0.3843138 0.3686275 0.3843138 0.9137255" },
					RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "-0.000 -0.153", OffsetMax = $"278.005 40.106" }
				}, "QuestCheckBox", "QuestProgresBar");
				container.Add(new CuiPanel
				{
					CursorEnabled = false,
					Image = { Color = "0.4462442 0.8679245 0.5786404 0.6137255" },
					RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "-0.000 -0.153", OffsetMax = $"{factor} 40.106" }
				}, "QuestProgresBar");
				container.Add(new CuiLabel
				{
					RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-139.005 -20.129", OffsetMax = "139.005 20.13" },
					Text = { Text = $"{foundQuest.Count} / {quests.ActionCount}", Font = "robotocondensed-bold.ttf", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
				}, "QuestProgresBar", "Progres");
			}

			#endregion

			#region PrizeList

			string prizeImage = _imageUI.GetImage("8");
			int i = 0;
			foreach (Quest.Prize prize in quests.PrizeList)
			{
				if(prize.IsHidden) continue;
				
				string prizeLayer = "QuestInfo" + $".{i}";
				
				int x = i % 4;
				int y = i / 4;
				
				container.Add(new CuiElement
				{
					Name = prizeLayer,
					Parent = "QuestInfoPanel",
					Components =
					{
						new CuiRawImageComponent { Color = "1 1 1 1", Png = prizeImage },
						new CuiRectTransformComponent
						{
							AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{23.42 + (x * 120.912)} {79.39 - (y * 78.345)}",
							OffsetMax = $"{129.555 + (x * 120.912)} {125.9 - (y * 78.345)}"
						}
					}
				});


				switch (prize.PrizeType)
				{
					case PrizeType.Item:
						container.Add(new CuiElement
						{
							Parent = prizeLayer,
							Components =
							{
								new CuiImageComponent { Color = "1 1 1 1", ItemId = ItemManager.FindItemDefinition(prize.ItemShortName).itemid },
								new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-10.059 -20.625", OffsetMax = "32.941 22.375" }
							}
						});
						break;
					case PrizeType.BluePrint:
						container.Add(new CuiElement
						{
							Parent = prizeLayer,
							Components =
							{
								new CuiImageComponent { Color = "1 1 1 1", ItemId = ItemManager.FindItemDefinition("blueprintbase").itemid },
								new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-10.059 -20.625", OffsetMax = "32.941 22.375" }
							}
						});
						container.Add(new CuiElement
						{
							Parent = prizeLayer,
							Components =
							{
								new CuiImageComponent { Color = "1 1 1 1", ItemId = ItemManager.FindItemDefinition(prize.ItemShortName).itemid },
								new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-10.059 -20.625", OffsetMax = "32.941 22.375" }
							}
						});
						break;
					case PrizeType.CustomItem:
						container.Add(new CuiElement
						{
							Parent = prizeLayer,
							Components =
							{
								new CuiImageComponent { Color = "1 1 1 1", ItemId = ItemManager.FindItemDefinition(prize.ItemShortName).itemid, SkinId = prize.ItemSkinID },
								new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-10.059 -20.625", OffsetMax = "32.941 22.375" }
							}
						});
						break;
					case PrizeType.Command:
						container.Add(new CuiElement
						{
							Parent = prizeLayer,
							Components =
							{
								new CuiRawImageComponent { Color = "1 1 1 1", Url = prize.CommandImageUrl },
								new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-10.059 -20.625", OffsetMax = "32.941 22.375" }
							}
						});
						break;
					default:
						throw new ArgumentOutOfRangeException();
				}

				container.Add(new CuiLabel
				{
					RectTransform = { AnchorMin = "1 0", AnchorMax = "1 0", OffsetMin = "-61.669 0.67", OffsetMax = "-5.931 17.33" },
					Text = { Text = $"x{prize.ItemAmount}", Font = "robotocondensed-regular.ttf", FontSize = 11, Align = TextAnchor.MiddleRight, Color = "1 1 1 1" }
				}, prizeLayer);
				
				if (y == 2)
				{
					break;
				}
				i++;
			}

			#endregion

			CuiHelper.AddUi(player, container);
		}

		#endregion

		#region MiniQuestList

		private void OpenMQL_CMD(BasePlayer player)
		{
			if (player == null)
				return;

			CheckPlayerCooldowns(player);
			MainUi(player);
		}

		[ChatCommand("quest")]
		private void QuestChatCommand(BasePlayer player, string command, string[] args)
		{
			if (args != null && args.Length > 0 && args[0].Equals("track", StringComparison.OrdinalIgnoreCase))
			{
				ToggleMiniQuestTracking(player);
				return;
			}

			OpenMQL_CMD(player);
		}


		private void QuestAliasCommand(BasePlayer player, string command, string[] args)
		{
			OpenMQL_CMD(player);
		}
		private void ToggleMiniQuestTracking(BasePlayer player)
		{
			if (player == null)
				return;

			if (_openMiniQuestListPlayers.Contains(player.userID))
			{
				_openMiniQuestListPlayers.Remove(player.userID);
				CuiHelper.DestroyUi(player, MINI_QUEST_LIST);
				return;
			}

			UIMiniQuestList(player);
		}

		private void UIMiniQuestList(BasePlayer player, int page = 0)
		{
			List<PlayerQuest> playerQuests = _playersInfo[player.userID].CurrentPlayerQuests;
			if (playerQuests == null)
			{
				return;
			}

			if (playerQuests.Count == 0)
			{
				SendReply(player, FormatMessageText("QUESTS_UI_ActiveQuestCount".GetAdaptedMessage(player.UserIDString), true));
				if (_openMiniQuestListPlayers.Contains(player.userID))
				{
					_openMiniQuestListPlayers.Remove(player.userID);
				}

				return;
			}

			if (!_openMiniQuestListPlayers.Contains(player.userID))
			{
				_openMiniQuestListPlayers.Add(player.userID);
			}

			playerQuests.Sort(delegate(PlayerQuest x, PlayerQuest y)
			{
				if (x.Finished && !y.Finished) return -1;
				if (!x.Finished && y.Finished) return 1;
				return 0;
			});
			string playerLaunguage = lang.GetLanguage(player.UserIDString);
			const int size = 72;
			string image = _imageUI.GetImage("5");
			string imageTwo = _imageUI.GetImage("13");
			int questCount = playerQuests.Count, qc = -72 * questCount;
			double ds = 207.912 + qc;
			CuiElementContainer container = new CuiElementContainer
			{
				{
					new CuiPanel
					{
						CursorEnabled = false,
						Image = { Color = "1 1 1 0" },
						RectTransform = { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = $"0 {ds}", OffsetMax = "304.808 303.288" }
					},
					"Overlay",
					MINI_QUEST_LIST, MINI_QUEST_LIST
				},

				{
					new CuiButton
					{
						Button = { Color = "0 0 0 0", Command = "CloseMiniQuestList" },
						Text = { Text = "x", Font = "robotocondensed-regular.ttf", FontSize = 15, Align = TextAnchor.MiddleCenter, Color = "1 0 0 1" },
						RectTransform = { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-20 -20", OffsetMax = "0 0" }
					},
					MINI_QUEST_LIST,
					"MiniQuestClosseBtn"
				},
				{
					new CuiLabel
					{
						RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "3.825 -23.035", OffsetMax = "173.821 0" },
						Text =
						{
							Text = "QUESTS_UI_ACTIVEOBJECTIVES".GetAdaptedMessage(player.UserIDString, playerQuests.Count), Font = "robotocondensed-bold.ttf", FontSize = 12,
							Align = TextAnchor.MiddleLeft, Color = "1 1 1 1"
						}
					},
					MINI_QUEST_LIST,
					"LabelMiniQuestPanel"
				}
			};


			int i = 0;
			foreach (PlayerQuest quest in playerQuests.Page(page, 8))
			{
				Quest currentQuest = _questList[quest.ParentQuestID];
				string color = quest.Finished ? "0.1960784 0.7176471 0.4235294 1" : "0.9490197 0.3764706 0.3960785 1";
				bool isDelivery = currentQuest.QuestType == QuestType.Delivery;
				container.Add(new CuiElement
				{
					Name = "MiniQuestImage",
					Parent = MINI_QUEST_LIST,
					Components =
					{
						new CuiRawImageComponent { Color = "0 0 0 1", Png = image },
						new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"3.829 {-90.188 - i * size}", OffsetMax = $"299.599 {-23.035 - i * size}" }
					}
				});
				container.Add(new CuiElement
				{
					Name = "ImgForMiniQuest",
					Parent = "MiniQuestImage",
					Components =
					{
						new CuiRawImageComponent { Color = color, Png = imageTwo },
						new CuiRectTransformComponent { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = "0.112 -33.576", OffsetMax = "12.577 33.577" }
					}
				});
				string qtext = isDelivery ? "QUESTS_UI_MiniQLInfoDelivery" : "QUESTS_UI_MiniQLInfo";
				container.Add(new CuiElement
				{
					Name = "LabelForMiniQuest",
					Parent = "MiniQuestImage",
					Components =
					{
						new CuiTextComponent
						{
							Text = qtext.GetAdaptedMessage(player.UserIDString, currentQuest.GetDisplayName(playerLaunguage), quest.Count, currentQuest.ActionCount, currentQuest.GetMissions(playerLaunguage)),
							Font = "robotocondensed-regular.ttf", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1"
						},
						new CuiOutlineComponent { Color = "0 0 0 1", Distance = "0.6 0.6" },
						new CuiRectTransformComponent { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = "14.925 -28.867", OffsetMax = "283.625 28.867" }
					}
				});
				i++;
			}

			#region Page

			int pageCount = (int)Math.Ceiling((double)playerQuests.Count / 8);
			if (pageCount > 1)
			{
				container.Add(new CuiPanel
				{
					CursorEnabled = false,
					Image = { Color = "1 1 1 0" },
					RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"3.829 {-126.593 - (i - 1) * size}", OffsetMax = $"145.353 {-90.187 - (i - 1) * size}" }
				}, MINI_QUEST_LIST, "Panel_1410");
				container.Add(new CuiLabel
				{
					RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-22.598 -11.514", OffsetMax = "21.517 11.514" },
					Text = { Text = $"{page + 1}/{pageCount}", Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
				}, "Panel_1410");
				if (page + 1 < pageCount)
				{
					container.Add(new CuiElement
					{
						Parent = "Panel_1410",
						Name = "DOWNBTN",
						Components =
						{
							new CuiRawImageComponent { Png = _imageUI.GetImage("4"), Color = "1 1 1 1" },
							new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-61.326 -13.326", OffsetMax = "-22.598 13.535" }
						}
					});

					container.Add(new CuiButton
					{
						RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
						Button = { Color = "0 0 0 0", Command = $"UI_Handler pageQLIST {page + 1}" },
						Text = { Text = "" }
					}, "DOWNBTN");
				}

				if (page > 0)
				{
					container.Add(new CuiElement
					{
						Parent = "Panel_1410",
						Name = "UPBTN",
						Components =
						{
							new CuiRawImageComponent { Png = _imageUI.GetImage("3"), Color = "1 1 1 1" },
							new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "21.517 -13.326", OffsetMax = "60.138 13.743" }
						}
					});

					container.Add(new CuiButton
					{
						RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
						Button = { Color = "0 0 0 0", Command = $"UI_Handler pageQLIST {page - 1}" },
						Text = { Text = "" }
					}, "UPBTN");
				}
			}

			#endregion

			CuiHelper.AddUi(player, container);
		}

		#endregion

		#region Notice

		private void UINottice(BasePlayer player, string msg, string sprite = "assets/icons/warning.png", string color = "0.76 0.34 0.10 1.00")
		{
			CuiElementContainer container = new CuiElementContainer
			{
				new CuiElement
				{
					FadeOut = 0.30f,
					Name = "QuestUiNotice",
					Parent = LAYER_MAIN_BACKGROUND,
					Components =
					{
						new CuiRawImageComponent { Color = "1 1 1 1", Png = _imageUI.GetImage("12"), FadeIn = 0.30f },
						new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "315 -110", OffsetMax = "610 -43" }
					}
				},

				new CuiElement
				{
					FadeOut = 0.30f,
					Name = "NoticeFeed",
					Parent = "QuestUiNotice",
					Components =
					{
						new CuiRawImageComponent { Color = color, Png = _imageUI.GetImage("13"), FadeIn = 0.30f },
						new CuiRectTransformComponent { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = "0.276 -33.458", OffsetMax = "12.692 33.459" }
					}
				},
				//container.Add(new CuiElement
				//{
				//    Parent = "QuestUi",
				//    Components = {
				//        new CuiRawImageComponent { Color = HexToRustFormat(color), Png = GetImage("16"), FadeIn = 0.30f },
				//        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "0.451 -23.243", OffsetMax = "1.3422 12.543" }
				//    }
				//});

				new CuiElement
				{
					FadeOut = 0.30f,
					Name = "NoticeSprite",
					Parent = "QuestUiNotice",
					Components =
					{
						new CuiImageComponent { Color = "1 1 1 1", Sprite = sprite, FadeIn = 0.30f },
						new CuiRectTransformComponent { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = "23.5 -15.5", OffsetMax = "54.5 15.5" }
					}
				},

				{
					new CuiLabel
					{
						RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-78.262 -33.458", OffsetMax = "143.522 33.459" },
						Text = { Text = FormatMessageText(msg), Font = "robotocondensed-regular.ttf", FontSize = 11, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1", FadeIn = 0.30f }
					},
					"QuestUiNotice",
					"NoticeText"
				}
			};

			CuiHelper.DestroyUi(player, "NoticeText");
			CuiHelper.DestroyUi(player, "NoticeSprite");
			CuiHelper.DestroyUi(player, "NoticeFeed");
			CuiHelper.DestroyUi(player, "QuestUiNotice");
			CuiHelper.AddUi(player, container);

			DeleteNotification(player);
		}

		private readonly Dictionary<BasePlayer, Timer> _playerTimer = new Dictionary<BasePlayer, Timer>();

		private void DeleteNotification(BasePlayer player)
		{
			Timer timers = timer.Once(3.5f, () =>
			{
				CuiHelper.DestroyUi(player, "NoticeText");
				CuiHelper.DestroyUi(player, "NoticeSprite");
				CuiHelper.DestroyUi(player, "NoticeFeed");
				CuiHelper.DestroyUi(player, "QuestUiNotice");
			});

			if (_playerTimer.ContainsKey(player))
			{
				if (_playerTimer[player] != null && !_playerTimer[player].Destroyed) _playerTimer[player].Destroy();
				_playerTimer[player] = timers;
			}
			else _playerTimer.Add(player, timers);
		}

		#endregion

		#endregion

		#region Helper Classes

		private static class ObjectCache
		{
			private static readonly object True = true;
			private static readonly object False = false;

			private static class StaticObjectCache<T>
			{
				private static readonly Dictionary<T, object> CacheByValue = new Dictionary<T, object>();

				public static object Get(T value)
				{
					object cachedObject;
					if (!CacheByValue.TryGetValue(value, out cachedObject))
					{
						cachedObject = value;
						CacheByValue[value] = cachedObject;
					}

					return cachedObject;
				}
			}

			public static object Get<T>(T value)
			{
				return StaticObjectCache<T>.Get(value);
			}

			public static object Get(bool value)
			{
				return value ? True : False;
			}
		}

		#endregion

		#region Command
		
		private void SendConsoleMessage(BasePlayer player, string message)
		{
			if(player != null)
				player.ConsoleMessage(FormatMessageText(message));
			else
				PrintWarning(FormatMessageText(message));
		}
		
		[ConsoleCommand("quests.player.reset")]
		private void PlayerDataReset(ConsoleSystem.Arg arg)
		{
			BasePlayer player = arg.Player();
			if (player != null && !player.IsAdmin)
			{
				player.ConsoleMessage("QUESTS_INSUFFICIENT_PERMISSIONS_ERROR".GetAdaptedMessage(player.UserIDString));
				return;
			}
			
			if (!arg.HasArgs())
			{
				SendConsoleMessage(player, "QUESTS_COMMAND_SYNTAX_ERROR".GetAdaptedMessage(PlayerOrNull(player)));
				return;
			}

			ulong playerid;
			if(!ulong.TryParse(arg.GetString(0), out playerid))
			{
				SendConsoleMessage(player, "QUESTS_INVALID_PLAYER_ID_INPUT".GetAdaptedMessage(PlayerOrNull(player)));
				return;
			}

			if (!playerid.IsSteamId())
			{
				SendConsoleMessage(player, "QUESTS_NOT_A_STEAM_ID".GetAdaptedMessage(PlayerOrNull(player)));
				return;
			}
			
			if (_playersInfo.ContainsKey(playerid))
			{
				_playersInfo[playerid] = new PlayerData();
				SendConsoleMessage(player, "QUESTS_PLAYER_PROGRESS_RESET".GetAdaptedMessage(PlayerOrNull(player)));
			}
			else
			{
				SendConsoleMessage(player, "QUESTS_PLAYER_NOT_FOUND_BY_STEAMID".GetAdaptedMessage(PlayerOrNull(player)));
			}
		}
		

		private static string PlayerOrNull(BasePlayer player) => player != null ? player.UserIDString : null;

		[ConsoleCommand("CloseMiniQuestList")]
		void CloseMiniQuestList(ConsoleSystem.Arg arg)
		{
			CuiHelper.DestroyUi(arg.Player(), MINI_QUEST_LIST);
			if (_openMiniQuestListPlayers.Contains(arg.Player().userID))
			{
				_openMiniQuestListPlayers.Remove(arg.Player().userID);
			}
		}

		[ConsoleCommand("CloseMainUI")]
		void CloseLayerPlayer(ConsoleSystem.Arg arg)
		{
			CuiHelper.DestroyUi(arg.Player(), LAYERS);
		}

		


		[ConsoleCommand("UI_Handler")]
		private void CmdConsoleHandler(ConsoleSystem.Arg args)
		{
			BasePlayer player = args.Player();
			List<PlayerQuest> playerQuests = _playersInfo[player.userID].CurrentPlayerQuests;
			if (playerQuests == null)
			{
				return;
			}

			if (player != null && args.HasArgs())
			{
				switch (args.Args[0])
				{
					case "get":
					{
						UICategory category;
						int pageIndex;
						if (args.HasArgs(4) && long.TryParse(args.Args[1],  out long questID) && Enum.TryParse(args.Args[2], out category) && int.TryParse(args.Args[3], out pageIndex))
						{
							Quest currentQuest = _questList[questID];
							if (currentQuest != null)
							{
								if (playerQuests.Count >= _config.settings.questCount)
								{
									UINottice(player, "QUESTS_UI_QuestLimit".GetAdaptedMessage(player.UserIDString));
									return;
								}

								if (playerQuests.Exists(p => p.ParentQuestID == currentQuest.QuestID))
								{
									UINottice(player, "QUESTS_UI_AlreadyTaken".GetAdaptedMessage(player.UserIDString));
									return;
								}

								if (!string.IsNullOrEmpty(currentQuest.QuestPermission) && !permission.UserHasPermission(player.UserIDString, $"{Name}." + currentQuest.QuestPermission))
								{
									UINottice(player, "QUESTS_UI_NotPerm".GetAdaptedMessage(player.UserIDString));
									return;
								}

								if (!currentQuest.IsRepeatable && _playersInfo[player.userID].CompletedQuestIds.Contains(currentQuest.QuestID))
								{
									UINottice(player, "QUESTS_UI_AlreadyDone".GetAdaptedMessage(player.UserIDString));
									return;
								}

								if (_playersInfo[player.userID].CompletedQuestIds.Contains(currentQuest.QuestID))
								{
									UINottice(player, "QUESTS_UI_AlreadyDone".GetAdaptedMessage(player.UserIDString));
									return;
								}

								playerQuests.Add(new PlayerQuest() { UserID = player.userID, ParentQuestID = currentQuest.QuestID, ParentQuestType = currentQuest.QuestType });
								_questStatistics.GatherTaskStatistics(TaskType.Taken);

								QuestListUI(player, category, pageIndex);
								QuestInfo(player, questID, category, pageIndex);
								UINottice(player, "QUESTS_UI_TookTasks".GetAdaptedMessage(player.UserIDString, currentQuest.GetDisplayName(lang.GetLanguage(player.UserIDString))));
							}
						}

						break;
					}
					case "page":
					{
						int pageIndex;
						UICategory category;
						if (int.TryParse(args.Args[1], out pageIndex) && Enum.TryParse(args.Args[2], out category))
						{
							QuestListUI(player, category, pageIndex);
						}

						break;
					}
					case "category":
					{
						UICategory category;
						bool isParsed = Enum.TryParse(args.Args[1], out category);
						if (isParsed)
						{
							Category(player, category);
							QuestListUI(player, category);
						}

						break;
					}
					case "pageQLIST":
					{
						int pageIndex;
						if (int.TryParse(args.Args[1], out pageIndex))
						{
							UIMiniQuestList(player, pageIndex);
						}

						break;
					}
					case "coldown":
					{
						UINottice(player, "QUESTS_UI_ACTIVECOLDOWN".GetAdaptedMessage(player.UserIDString));
						break;
					}
					case "questinfo":
					{
						long questIndex;
						UICategory category;
						int pageIndex;
						if (long.TryParse(args.Args[1], out questIndex) && Enum.TryParse(args.Args[2], out category) && int.TryParse(args.Args[3], out pageIndex))
						{
							QuestInfo(player, questIndex, category, pageIndex);
						}

						break;
					}
					case "finish":
					{
						long questID;
						UICategory category;
						int pageIndex;
						bool cancel = false;
						if (args.HasArgs(5))
						{
							bool parsed = bool.TryParse(args.Args[4], out bool tmp);
							cancel = parsed && tmp;
						}
						if (args.HasArgs(4) && long.TryParse(args.Args[1], out questID) && Enum.TryParse(args.Args[2], out category) && int.TryParse(args.Args[3], out pageIndex))
						{
							Quest globalQuest = _questList[questID];
							if (globalQuest != null)
							{
								PlayerQuest currentQuest = playerQuests.Find(quest => quest.ParentQuestID == globalQuest.QuestID);
								if (currentQuest == null)
								{
									return;
								}

								if (currentQuest.Finished || (currentQuest.ParentQuestType == QuestType.Delivery && !cancel))
								{
									int count = 0;
									foreach (Quest.Prize prize in globalQuest.PrizeList)
										if (prize.PrizeType != PrizeType.Command)
											count++;

									if (24 - player.inventory.containerMain.itemList.Count < count)
									{
										UINottice(player, "QUESTS_UI_LackOfSpace".GetAdaptedMessage(player.UserIDString));
										return;
									}

									if (globalQuest.IsReturnItemsRequired)
									{
										ulong skins;
										if (globalQuest.QuestType is QuestType.Loot or QuestType.Delivery && ulong.TryParse(globalQuest.Target, out skins))
										{
											if (!TakeSkinIdItemsForQuest(player, globalQuest, skins))
												return;
										}
										else if (globalQuest.QuestType is QuestType.Gather or QuestType.Loot or QuestType.Craft or QuestType.PurchaseFromNpc or QuestType.Growseedlings or QuestType.Fishing or QuestType.Delivery)
										{
											if (!TakeItemsNeededForQuest(player, globalQuest))
												return;
										}
									}

									UINottice(player, "QUESTS_UI_QuestsCompleted".GetAdaptedMessage(player.UserIDString));

									currentQuest.Finished = false;
									GiveQuestReward(player, globalQuest.PrizeList);
									if (!globalQuest.IsRepeatable)
									{
										_playersInfo[player.userID].CompletedQuestIds.Add(currentQuest.ParentQuestID);
									}
									else if (globalQuest.Cooldown > 0)
									{
										_playersInfo[player.userID].PlayerQuestCooldowns[currentQuest.ParentQuestID] = CurrentTime() + globalQuest.Cooldown;
									}

									playerQuests.Remove(currentQuest);
									QuestListUI(player, category, pageIndex);
									QuestInfo(player, questID, category, pageIndex);

									if (currentQuest.ParentQuestType == QuestType.Delivery)
									{
										if (Instance._config.settings.sandNotifyAllPlayer)
										{
											foreach (BasePlayer players in BasePlayer.activePlayerList)
											{
												Instance.SendChat(players, "QUESTS_Finished_QUEST_ALL".GetAdaptedMessage( players.UserIDString, player.displayName,
													globalQuest.GetDisplayName(Instance.lang.GetLanguage(player.UserIDString))));
											}
										}

										Interface.CallHook("OnQuestCompleted", player, globalQuest.GetDisplayName(Instance.lang.GetLanguage(player.UserIDString)));
										Instance._questStatistics.GatherTaskStatistics(TaskType.TaskExecution, globalQuest.QuestID);
										Instance._questStatistics.GatherTaskStatistics(TaskType.Completed);
									}
								}
								else
								{
									UINottice(player, "QUESTS_UI_PassedTasks".GetAdaptedMessage(player.UserIDString));
									playerQuests.Remove(currentQuest);
									QuestListUI(player, category, pageIndex);
									QuestInfo(player, questID, category, pageIndex);
									_questStatistics.GatherTaskStatistics(TaskType.Declined);
								}
							}
							else
							{
								UINottice(player, " <color=#4286f4> </color>  !");
							}
						}

						break;
					}
				}
			}
		}

		#endregion

		#region HelpQuestsMetods

		#region Rewards and bring items

		private void GiveQuestReward(BasePlayer player, List<Quest.Prize> prizeList)
		{
			foreach (Quest.Prize check in prizeList)
			{
				switch (check.PrizeType)
				{
					case PrizeType.Item:
						Item newItem = ItemManager.CreateByPartialName(check.ItemShortName, check.ItemAmount);
						player.GiveItem(newItem, BaseEntity.GiveItemReason.Crafted);
						break;
					case PrizeType.Command:
						Server.Command(check.PrizeCommand.Replace("%STEAMID%", player.UserIDString));
						break;
					case PrizeType.CustomItem:
						Item customItem = ItemManager.CreateByPartialName(check.ItemShortName, check.ItemAmount, check.ItemSkinID);
						customItem.name = check.CustomItemName;
						player.GiveItem(customItem, BaseEntity.GiveItemReason.Crafted);
						break;
					case PrizeType.BluePrint:
						Item itemBp = ItemManager.Create(ItemManager.blueprintBaseDef);
						ItemDefinition targetItem = ItemManager.FindItemDefinition(check.ItemShortName);
						if (targetItem == null) continue;
						itemBp.blueprintTarget = targetItem.isRedirectOf != null ? targetItem.isRedirectOf.itemid : targetItem.itemid;
						player.GiveItem(itemBp, BaseEntity.GiveItemReason.Crafted);
						break;
					default:
						throw new ArgumentOutOfRangeException();
				}
			}
		}

		private bool TakeItemsNeededForQuest(BasePlayer player, Quest globalQuest)
		{
			ItemDefinition idItem = ItemManager.FindItemDefinition(globalQuest.Target);
			int? item = null;
			if (player != null && player.inventory != null)
				item = player.inventory.GetAmount(idItem.itemid);
			
			if (item is 0 or null)
			{
				UINottice(player, "QUESTS_UI_InsufficientResources".GetAdaptedMessage(player.UserIDString, idItem.displayName.english));
				return false;
			}

			if (item < globalQuest.ActionCount)
			{
				UINottice(player, "QUESTS_UI_NotResourcesAmount".GetAdaptedMessage(player.UserIDString, idItem.displayName.english, globalQuest.ActionCount));
				return false;
			}

			if (item >= globalQuest.ActionCount)
			{
				player.inventory.Take(null, idItem.itemid, globalQuest.ActionCount);
			}

			return true;
		}

		private bool TakeSkinIdItemsForQuest(BasePlayer player, Quest globalQuest, ulong skins)
		{
			List<Item> acceptedItems = Pool.Get<List<Item>>();
			int itemAmount = 0;
			int amountQuest = globalQuest.ActionCount;
			string itemName = string.Empty;
			List<Item> items = Pool.Get<List<Item>>();
			player.inventory.GetAllItems(items);
			foreach (Item item in items)
			{
				if (item.skin == skins)
				{
					acceptedItems.Add(item);
					itemAmount += item.amount;
					itemName = item.GetName();
				}
			}
			Pool.Free(ref items);
			if (acceptedItems.Count == 0)
			{
				UINottice(player, "QUESTS_UI_InsufficientResourcesSkin".GetAdaptedMessage(player.UserIDString));
				return false;
			}

			if (itemAmount < amountQuest)
			{
				UINottice(player, "QUESTS_UI_NotResourcesAmount".GetAdaptedMessage(player.UserIDString, itemName, amountQuest));
				return false;
			}

			foreach (Item use in acceptedItems)
			{
				if (use.amount >= amountQuest)
				{
					use.amount -= amountQuest;
					if (use.amount == 0)
					{
						use.RemoveFromContainer();
						use.Remove();
					}

					amountQuest = 0;
				}
				else
				{
					amountQuest -= use.amount;
					use.RemoveFromContainer();
					use.Remove();
				}

				if (amountQuest == 0)
				{
					break;
				}
			}
			
			Pool.Free(ref acceptedItems);
			player.inventory.SendSnapshot();
			return true;
		}

		#endregion

		#region QuestProgress

		private void QuestProgress(ulong playerUserID, QuestType questType, string entName = "", string skinId = "", List<Item> items = null, int count = 1)
		{
			if (!_playersInfo.TryGetValue(playerUserID, out PlayerData playerData))
				return;

			List<PlayerQuest> playerQuests = playerData.CurrentPlayerQuests.FindAll(x => x.ParentQuestType == questType && !x.Finished);

			foreach (PlayerQuest quest in playerQuests)
			{
				Quest parentQuest = _questList[quest.ParentQuestID];
				if (string.IsNullOrEmpty(entName) && items == null)
				{
					quest.AddCount(count);
					return;
				}
				
				if (items != null)
				{
					bool isSkinID = ulong.TryParse(parentQuest.Target, out ulong skinIditem);
					foreach (Item item in items)
					{
						if(item.info.shortname.Equals(parentQuest.Target, StringComparison.OrdinalIgnoreCase) || (isSkinID && item.skin.Equals(skinIditem)))
							quest.AddCount(item.amount);
					}
					continue;
				}
				
				switch (questType)
				{
					case QuestType.IQCases:
					case QuestType.HarborEvent:
					case QuestType.SatelliteDishEvent:
					case QuestType.Sputnik:
					case QuestType.Caravan:
					case QuestType.Convoy:
					case QuestType.GasStationEvent:
					case QuestType.FerryTerminalEvent:
					case QuestType.Triangulation:
					case QuestType.AbandonedBases:
					case QuestType.IQDronePatrol:
					case QuestType.IQDefenderSupply:
					case QuestType.IQHeadReward:
					{
						if (parentQuest.Target.Equals(entName) || parentQuest.Target.Equals("0") || parentQuest.Target.Equals("999"))
							quest.AddCount(count);
						break;
					}
					case QuestType.Swipe:
					{
						if (parentQuest.Target.Equals(entName))
							quest.AddCount(count);
						break;
					}
					case QuestType.EntityKill:
					{
						string normalizedEntityName = entName.ToLowerInvariant();
						if (parentQuest.Targets.Contains(normalizedEntityName) || normalizedEntityName.Equals(parentQuest.Target, StringComparison.OrdinalIgnoreCase))
							quest.AddCount(count);
						break;
					}
					default:
					{
						if (entName.Equals(parentQuest.Target, StringComparison.OrdinalIgnoreCase) || skinId.Equals(parentQuest.Target))
							quest.AddCount(count);
						break;
					}
				}
			}
			
			Interface.CallHook("OnQuestProgress", playerUserID, (int)questType, entName, skinId, items, count);
		}

		#endregion

		#endregion

		#region ImageLoader

		private class ImageUI
		{
			private readonly string _paths;
			private readonly string _printPath;
			private readonly Dictionary<string, ImageData> _images;
			private bool _missingImagesNotified;

			private enum ImageStatus
			{
				NotLoaded,
				Loaded,
				Failed
			}

			public ImageUI()
			{
				_paths = Instance.Name + "/Images/";
				_printPath = "data/" + _paths;
				_images = new Dictionary<string, ImageData>
				{
					{ "1", new ImageData() },
					{ "2", new ImageData() },
					{ "3", new ImageData() },
					{ "4", new ImageData() },
					{ "5", new ImageData() },
					{ "6", new ImageData() },
					{ "7", new ImageData() },
					{ "8", new ImageData() },
					{ "9", new ImageData() },
					{ "10", new ImageData() },
					{ "11", new ImageData() },
					{ "12", new ImageData() },
					{ "13", new ImageData() },
					{ "14", new ImageData() },
					{ "15", new ImageData() },
					{ "16", new ImageData() }
				};
			}

			private class ImageData
			{
				public ImageStatus Status = ImageStatus.NotLoaded;
				public string Id { get; set; }
			}

			public string GetImage(string name)
			{
				ImageData image;
				if (_images.TryGetValue(name, out image) && image.Status == ImageStatus.Loaded)
					return image.Id;
				return null;
			}

			public void DownloadImage()
			{
				KeyValuePair<string, ImageData>? image = null;
				foreach (KeyValuePair<string, ImageData> img in _images)
				{
					if (img.Value.Status == ImageStatus.NotLoaded)
					{
						image = img;
						break;
					}
				}

				if (image != null)
				{
					ServerMgr.Instance.StartCoroutine(ProcessDownloadImage(image.Value));
				}
				else
				{
					List<string> failedImages = new List<string>();

					foreach (KeyValuePair<string, ImageData> img in _images)
					{
						if (img.Value.Status == ImageStatus.Failed)
						{
							failedImages.Add(img.Key);
						}
					}

					if (failedImages.Count > 0)
					{
						if (!_missingImagesNotified)
						{
							_missingImagesNotified = true;
							string images = string.Join(", ", failedImages);
							Instance.Puts($"Optional image files are missing ({images}). UI icons will use defaults until files are added to '{_printPath}'.");
						}
					}
					else
					{
						Instance.Puts($"{_images.Count} images downloaded successfully!");
					}
				}
			}

			public void UnloadImages()
			{
				foreach (KeyValuePair<string, ImageData> item in _images)
					if (item.Value.Status == ImageStatus.Loaded)
						if (item.Value?.Id != null)
							FileStorage.server.Remove(uint.Parse(item.Value.Id), FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID);

				_images?.Clear();
			}

			private IEnumerator ProcessDownloadImage(KeyValuePair<string, ImageData> image)
			{
				string url = "file://" + Interface.Oxide.DataDirectory + "/" + _paths + image.Key + ".png";

				using UnityWebRequest www = UnityWebRequestTexture.GetTexture(url);
				yield return www.SendWebRequest();

				if (www.result is UnityWebRequest.Result.ConnectionError or UnityWebRequest.Result.ProtocolError)
				{
					image.Value.Status = ImageStatus.Failed;
				}
				else
				{
					Texture2D tex = DownloadHandlerTexture.GetContent(www);
					image.Value.Id = FileStorage.server.Store(tex.EncodeToPNG(), FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID).ToString();
					image.Value.Status = ImageStatus.Loaded;
					UnityEngine.Object.DestroyImmediate(tex);
				}

				DownloadImage();
			}
		}

		#endregion

	}
}

namespace Oxide.Plugins.QuestsExtensionMethods
{
	public static class ExtensionMethods
	{
		private static readonly Lang Lang = Interface.Oxide.GetLibrary<Lang>();

		#region GetLang
		
		public static string GetAdaptedMessage(this string langKey, in string userID, params object[] args)
		{
			string message = Lang.GetMessage(langKey, Quests.Instance, userID);
			
			StringBuilder stringBuilder = Pool.Get<StringBuilder>();
		
			try
			{
				return stringBuilder.AppendFormat(message, args).ToString();
			}
			finally
			{
				stringBuilder.Clear();
				Pool.FreeUnmanaged(ref stringBuilder);
			}
		}
		
		public static string GetAdaptedMessage(this string langKey, in string userID = null)
		{
			return Lang.GetMessage(langKey, Quests.Instance, userID);
		}
		
		#endregion
		
		#region Pagination

		public static IEnumerable<T> Page<T>(this List<T> source, int page, int pageSize)
		{
			int start = page * pageSize;
			int end = start + pageSize;
			for (int i = start; i < end && i < source.Count; i++)
			{
				yield return source[i];
			}
		}

		#endregion
	}
}