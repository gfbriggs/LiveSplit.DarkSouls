﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.Remoting.Channels;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.DarkSouls.Controls;
using LiveSplit.DarkSouls.Data;
using LiveSplit.DarkSouls.Memory;
using LiveSplit.Model;
using LiveSplit.UI;
using LiveSplit.UI.Components;

namespace LiveSplit.DarkSouls
{
	public class SoulsComponent : IComponent
	{
		public const string DisplayName = "Dark Souls Autosplitter";

		private TimerModel timer;
		private SplitCollection splitCollection;
		private SoulsMemory memory;
		private SoulsMasterControl masterControl;
		private Dictionary<SplitTypes, Func<int[], bool>> splitFunctions;
		private Vector3[] covenantLocations;
		private Vector3[] bonfireLocations;
		private RunState run;

		private bool preparedForWarp;
		private bool isLoadScreenVisible;

		// This variable tracks whether the player confirmed a warp from a bonfire prompt. The data used to detect this
		// state (beginning a bonfire warp) doesn't persist up to the loading screen's appearance, so it needs to be
		// tracked separately.
		private bool isBonfireWarpConfirmed;

		// Most warp splits detect an event, then wait for a warp to occur. Bonfire and item warps are unique in that
		// they can be undone once activated (by leaving the target bonfire or losing the target item).
		private bool isBonfireWarpSplitActive;
		private bool isItemWarpSplitActive;

		// The estus flask is more complex than other items. Conceptually, you have a single estus flask (either empty
		// or filled) that can be upgraded to a maximum of +7. Internally, though, each separate flask has its own ID
		// (16 total). Some special code is required to deal with that fact.
		private bool isEstusSplit;

		// If a particular run doesn't ever split on items, it would be wasteful to track them.
		private bool itemsEnabled;

		public SoulsComponent()
		{
			splitCollection = new SplitCollection();
			memory = new SoulsMemory();
			masterControl = new SoulsMasterControl();
			run = new RunState();

			splitFunctions = new Dictionary<SplitTypes, Func<int[], bool>>
			{
				{ SplitTypes.Bonfire, ProcessBonfire },
				{ SplitTypes.Boss, ProcessBoss },
				{ SplitTypes.Covenant, ProcessCovenant },
				{ SplitTypes.Event, ProcessEvent },
				{ SplitTypes.Flag, ProcessFlag },
				{ SplitTypes.Item, ProcessItem }
			};

			// This array is used for covenant discovery splits. Discovery occurs when the player is prompted to join a
			// covenant via a yes/no confirmation box. That prompt's appearance can be detected through memory, but
			// it's shared among all covenants. As such, position is used to narrow down the covenant.
			covenantLocations = new []
			{
				new Vector3(-28, -53, 87), // Way of White (in front of Petrus)
				new Vector3(9, 29, 121), // Way of White (beside Rhea) 
				new Vector3(622, 164, 255), // Princess (in front of Gwynevere)
				new Vector3(36, 12, -32), // Sunlight (in front of the sunlight altar) 
				new Vector3(93, -311, 4), // Darkwraith (in front of Kaathe)
				new Vector3(-702, -412, -333), // Dragon (in front of the everlasting dragon)
				new Vector3(-161, -265, -32), // Gravelord (below Nito)
				new Vector3(285, -3, -105), // Forest (below Alvina) 
				new Vector3(430, 60, 255), // Darkmoon (just outside Gwyndolin's boss arena)
				new Vector3(138, -252, 94) // Chaos (in front of the fair lady)
			};

			// Previously, bonfire resting was determined by reading the last bonfire from memory (i.e. the bonfire to
			// which the player will warp on death or when using an item). That approach works great for most cases,
			// but there's a catch in that the last bonfire seems to update a frame after the resting animation is
			// detected. As a result, it was possible for the autosplitter to split incorrectly if that last bonfire
			// value is already set to the target (in which case you'd split at any bonfire, not just the target).
			// There's unfortunately no foolproof solution to this problem using that last bonfire data alone, which is
			// why position data is used instead.
			bonfireLocations = new []
			{
				new Vector3(0, 0, 0), 
			};
		}

		public string ComponentName => DisplayName;

		public float HorizontalWidth => 0;
		public float MinimumHeight => 0;
		public float VerticalHeight => 0;
		public float MinimumWidth => 0;
		public float PaddingTop => 0;
		public float PaddingBottom => 0;
		public float PaddingLeft => 0;
		public float PaddingRight => 0;

		public IDictionary<string, Action> ContextMenuControls => null;

		public void DrawHorizontal(Graphics g, LiveSplitState state, float height, Region clipRegion)
		{
		}

		public void DrawVertical(Graphics g, LiveSplitState state, float width, Region clipRegion)
		{
		}

		public Control GetSettingsControl(LayoutMode mode)
		{
			return masterControl;
		}

		public XmlNode GetSettings(XmlDocument document)
		{
			XmlElement root = document.CreateElement("Settings");
			XmlElement igtElement =
				document.CreateElementWithInnerText("UseGameTime", masterControl.UseGameTime.ToString());
			XmlElement splitsElement = document.CreateElement("Splits");

			var splits = masterControl.CollectionControl.ExtractSplits();
			splitCollection.Splits = splits;

			// Splits can be null if the user hasn't added any splits through the LiveSplit UI.
			if (splits != null)
			{
				foreach (var split in splits)
				{
					var data = split.Data;
					var dataString = data != null ? string.Join("|", data) : "";

					XmlElement splitElement = document.CreateElement("Split");
					splitElement.AppendChild(document.CreateElementWithInnerText("Type", split.Type.ToString()));
					splitElement.AppendChild(document.CreateElementWithInnerText("Data", dataString));
					splitsElement.AppendChild(splitElement);
				}
			}

			root.AppendChild(igtElement);
			root.AppendChild(splitsElement);

			return root;
		}

		public void SetSettings(XmlNode settings)
		{
			bool useGameTime = bool.Parse(settings["UseGameTime"].InnerText);

			XmlNodeList splitNodes = settings["Splits"].GetElementsByTagName("Split");
			Split[] splits = new Split[splitNodes.Count];

			for (int i = 0; i < splitNodes.Count; i++)
			{
				var splitNode = splitNodes[i];
				var type = (SplitTypes)Enum.Parse(typeof(SplitTypes), splitNode["Type"].InnerText);

				string rawData = splitNode["Data"].InnerText;

				int[] data = null;

				if (rawData.Length > 0)
				{
					string[] dataTokens = splitNode["Data"].InnerText.Split('|');

					data = new int[dataTokens.Length];

					for (int j = 0; j < dataTokens.Length; j++)
					{
						data[j] = int.Parse(dataTokens[j]);
					}
				}

				splits[i] = new Split(type, data);
			}

			splitCollection.Splits = splits;
			masterControl.Refresh(splits, useGameTime);
		}

		public void Update(IInvalidator invalidator, LiveSplitState state, float width, float height, LayoutMode mode)
		{
			if (timer == null)
			{
				timer = new TimerModel();
				timer.CurrentState = state;

				state.OnStart += (sender, args) =>
				{
					var splits = splitCollection.Splits;

					itemsEnabled = splits.Any(s => s.Type == SplitTypes.Item);

					if (itemsEnabled)
					{
						memory.SetItems(splits.Select(ComputeItemId).ToList());
					}

					splitCollection.OnStart();
					UpdateRunState();
				};

				state.OnSplit += (sender, args) =>
				{
					splitCollection.OnSplit();
					UpdateRunState();
				};

				state.OnUndoSplit += (sender, args) =>
				{
					splitCollection.OnUndoSplit();
					UpdateRunState();
				};

				state.OnSkipSplit += (sender, args) =>
				{
					splitCollection.OnSkipSplit();
					UpdateRunState();
				};

				state.OnReset += (sender, value) => { splitCollection.OnReset(); };
			}

			Refresh(state.CurrentPhase);
		}

		private void UpdateRunState()
		{
			Split split = splitCollection.CurrentSplit;

			if (split == null || split.Type == SplitTypes.Manual || !split.IsFinished)
			{
				return;
			}

			preparedForWarp = false;
			isBonfireWarpConfirmed = false;
			isBonfireWarpSplitActive = false;
			isItemWarpSplitActive = false;
			isEstusSplit = false;

			int[] data = split.Data;

			switch (split.Type)
			{
				case SplitTypes.Bonfire:
					int bonfireIndex = data[0];
					int criteria = data[1];

					bool onRest = criteria == 1;
					bool onWarp = criteria == 5;

					if (onRest || onWarp)
					{
						run.Target = bonfireIndex;
						isBonfireWarpSplitActive = onWarp;
					}
					else
					{
						run.Id = Flags.OrderedBonfires[bonfireIndex];
						run.Data = (int)memory.GetBonfireState((BonfireFlags)run.Id);
						run.Target = Flags.OrderedBonfireStates[data[1]];
					}

					break;

				case SplitTypes.Boss:
					run.Id = Flags.OrderedBosses[data[0]];
					run.Flag = memory.CheckFlag(run.Id);

					break;

				case SplitTypes.Covenant:
					// The first and third options involve discovery, while the second and fourth involve joining.
					bool onDiscover = data[1] % 2 == 0;

					if (onDiscover)
					{
						run.Data = memory.GetPromptedMenu();
						run.Target = Flags.OrderedCovenants[data[0]];
					}
					else
					{
						run.Data = (int)memory.GetCovenant();
						run.Target = Flags.OrderedCovenants[data[0]];
					}

					break;

				case SplitTypes.Event:
					switch ((WorldEvents)data[0])
					{
						case WorldEvents.Bell1:
							run.Id = (int)BellFlags.FirstBell;
							run.Flag = memory.CheckFlag(run.Id);

							break;

						case WorldEvents.Bell2:
							run.Id = (int)BellFlags.SecondBell;
							run.Flag = memory.CheckFlag(run.Id);

							break;

						default: run.Data = memory.GetClearCount(); break;
					}

					break;

				case SplitTypes.Flag:
					int flag = data[0];

					run.Id = flag;
					run.Flag = memory.CheckFlag(flag);

					break;

				case SplitTypes.Item:
					ItemId id = ComputeItemId(split);

					int baseId = id.BaseId;
					int mods = data[2];
					int reinforcement = data[3];
					int count = data[4];

					isItemWarpSplitActive = data[5] == 1;

					// This spans the range of all possible estus IDs (unfilled +0 through unfilled +7).
					isEstusSplit = baseId >= 200 && baseId <= 214;

					// In the layout file, mods and reinforcement are stored as int.MaxValue to simplify split
					// validation.
					mods = mods == int.MaxValue ? 0 : mods;
					reinforcement = reinforcement == int.MaxValue ? 0 : reinforcement;

					// The data field of the run state isn't otherwise used for item splits, so it's used to store item
					// category (required to differentiate between items with the same ID).
					run.Id = id.BaseId;
					run.Data = id.Category;
					run.ItemTarget = new ItemState(mods, reinforcement, count);

					break;

				case SplitTypes.Zone:
					break;
			}
		}

		private Tuple<int, int> pair = new Tuple<int, int>(-1, -1);

		// Making the phase nullable makes testing easier.
		public void Refresh(TimerPhase? phase = null) 
		{
			if (!Hook())
			{
				return;
			}

			int forcedAnimation = memory.GetForcedAnimation();
			int lastBonfire = memory.GetLastBonfire();

			if (pair.Item1 != forcedAnimation || pair.Item2 != lastBonfire)
			{
				pair = new Tuple<int, int>(forcedAnimation, lastBonfire);

				Console.WriteLine($"Pair: [{forcedAnimation}, {(BonfireFlags)lastBonfire}]");
			}

			return;

			if (phase != null)
			{
				TimerPhase value = phase.Value;

				if (value == TimerPhase.NotRunning || value == TimerPhase.Ended)
				{
					return;
				}
			}
			
			Split split = splitCollection.CurrentSplit;

			// It's possible for the current split to be null if no splits were configured at all.
			if (split == null || split.Type == SplitTypes.Manual || !split.IsFinished)
			{
				return;
			}

			/*
			// The timer is intentionally updated before an autosplit occurs (to ensure the split time is as accurate
			// as possible).
			if (masterControl.UseGameTime)
			{
				int gameTime = memory.GetGameTimeInMilliseconds();
				int previousTime = run.GameTime;
				int previousTime = run.GameTime;

				run.GameTime = gameTime;

				// This condition is only possible during a run when game time isn't increasing (game time resets to
				// zero on the main menu).
				bool pause = gameTime == 0 && previousTime > 0;
				bool unpause = previousTime == 0 && gameTime > 0;

				if (pause && phase == TimerPhase.Running)
				{
					timer.Pause();
					state.IsGameTimePaused = true;
				}
				else if (unpause && phase == TimerPhase.Paused)
				{
					timer.UndoAllPauses();
					state.IsGameTimePaused = false;
				}

				int max = Math.Max(gameTime, runTime);

				state.SetGameTime(TimeSpan.FromMilliseconds(max));
				run.GameTime = gameTime;
				run.MaxGameTime = max;
			}
			*/

			// This is called each tick regardless of whether the current split is an item split (to ensure that the
			// inventory state is accurate by the time an item split crops up).
			if (itemsEnabled)
			{
				memory.RefreshItems();
			}

			// This condition covers all split types with warping as an option.
			if (preparedForWarp)
			{
				if (isBonfireWarpSplitActive)
				{
					int[] leaveValues =
					{
						(int)AnimationFlags.BonfireLeave1,
						(int)AnimationFlags.BonfireLeave2,
						(int)AnimationFlags.BonfireLeave3
					};

					int animation = memory.GetForcedAnimation();

					// Without this check, the player could rest at a target bonfire (without warping), then warp from
					// another bonfire and have the tool incorrectly split.
					if (leaveValues.Contains(animation))
					{
						preparedForWarp = false;

						return;
					}
				}
				else if (isItemWarpSplitActive && !IsTargetItemSatisfied())
				{
					preparedForWarp = false;

					return;
				}

				if (CheckWarp())
				{
					// Timer is null when testing the program from the testing class.
					timer?.Split();
				}

				return;
			}

			if (splitFunctions[split.Type](split.Data))
			{
				timer?.Split();
			}
		}

		private bool Hook()
		{
			bool previouslyHooked = memory.ProcessHooked;

			// It's possible for the timer to be running before Dark Souls is launched. In this case, all splits are
			// treated as manual until the process is hooked, at which point the run state is updated appropriately.
			if (memory.Hook() && !previouslyHooked && timer?.CurrentState.CurrentPhase == TimerPhase.Running)
			{
				UpdateRunState();
			}

			return memory.ProcessHooked;
		}

		private ItemId ComputeItemId(Split split)
		{
			const int EstusId = 200;

			int[] data = split.Data;
			int rawId = ItemFlags.MasterList[data[0]][data[1]];
			int digit = rawId;
			int divisor = 1;

			// See https://stackoverflow.com/a/701355/7281613.
			while (digit > 10)
			{
				digit /= 10;
				divisor *= 10;
			}

			// Many items have a category of zero.
			int baseId = rawId % divisor;
			int category = digit == 9 ? 0 : digit;

			// Each type of estus flask (+0 to +7 and empty or filled) has its own ID in memory (ranging from 200
			// through 215 inclusive). Since either an empty or filled flask counts as acquiring the item, the empty ID
			// is stored as the target, then both that and the filled ID (empty ID + 1) are queried each tick.
			if (baseId == EstusId)
			{
				// Note that by the time this function is called, the split is guaranteed to be finished, meaning that
				// the reinforcement dropdown will always have a valid value.
				int reinforcement = data[3];

				baseId += reinforcement * 2;
			}

			return new ItemId(baseId, category);
		}

		private void PrepareWarp()
		{
			preparedForWarp = true;
			isLoadScreenVisible = memory.IsLoadScreenVisible();
		}

		private bool CheckWarp()
		{
			const int Darksign = 117;
			const int HomewardBone = 330;
			const int BonfireWarpPrompt = 80;

			if (!isBonfireWarpConfirmed)
			{
				// This state becomes true for just a moment when the player confirms a bonfire warp.
				isBonfireWarpConfirmed = memory.GetPromptedMenu() == BonfireWarpPrompt &&
					memory.GetForcedAnimation() == (int)AnimationFlags.BonfireWarp;
			}

			bool visible = memory.IsLoadScreenVisible();
			bool previouslyVisible = isLoadScreenVisible;

			isLoadScreenVisible = visible;

			if (!visible || previouslyVisible)
			{
				return false;
			}

			// Note that for bonfire warp splits, this point will only be reached if the player is resting at the
			// correct bonfire.
			if (isBonfireWarpConfirmed)
			{
				isBonfireWarpConfirmed = false;

				return true;
			}

			// This point in the code can only be reached via an on-warp bonfire split (meaning that warp items are
			// irrelevant).
			if (splitCollection.CurrentSplit.Type == SplitTypes.Bonfire)
			{
				return false;
			}

			int itemUsed = memory.GetPromptedItem();

			return itemUsed == Darksign || itemUsed == HomewardBone;
		}

		private bool ProcessBonfire(int[] data)
		{
			// The player must be very close to a bonfire in order to rest. The chosen value here is arbitrary and
			// could be smaller (but it makes no performance difference).
			const int Radius = 10;

			int criteria = data[1];

			bool onRest = criteria == 1;
			bool onWarp = criteria == 5;

			if (onRest || onWarp)
			{
				int[] restValues =
				{
					(int)AnimationFlags.BonfireSit1,
					(int)AnimationFlags.BonfireSit2,
					(int)AnimationFlags.BonfireSit3
				};

				int animation = memory.GetForcedAnimation();

				// This confirms that the player is resting at a bonfire (but not which bonfire).
				if (restValues.Contains(animation))
				{
					int index = ComputeClosestTarget(bonfireLocations, Radius);
					
					bool correctBonfire = index == run.Target;

					if (correctBonfire && onWarp)
					{
						PrepareWarp();

						return false;
					}

					return correctBonfire;
				}

				return false;
			}

			int state = (int)memory.GetBonfireState((BonfireFlags)run.Id);

			// Increasing bonfires states (unlit, lit, then the different levels of kindling) always increase the state
			// value.
			if (run.Data > state)
			{
				run.Data = state;

				return state == run.Target;
			}

			return false;
		}

		private bool ProcessBoss(int[] data)
		{
			bool isDefeated = memory.CheckFlag(run.Id);

			if (isDefeated && !run.Flag)
			{
				run.Flag = true;

				bool onVictory = data[1] == 0;

				if (onVictory)
				{
					return true;
				}

				PrepareWarp();
			}

			return false;
		}

		private bool ProcessCovenant(int[] data)
		{
			int criteria = data[1];

			bool onDiscovery = criteria % 2 == 0;
			bool preWarpSatisfied = onDiscovery ? CheckCovenantDiscovery() : CheckCovenantJoin();

			if (preWarpSatisfied)
			{
				bool onWarp = criteria >= 2;

				if (onWarp)
				{
					PrepareWarp();

					return false;
				}

				return true;
			}

			return false;
		}

		private bool CheckCovenantDiscovery()
		{
			// This radius is arbitrary and could be smaller. All that matters is that the radius is large enough to
			// account for the maximum distance between any two points from which the player could join a single
			// covenant. For reference, the largest distance I could find is within the area surrounding the ancient
			// dragon in Ash Lake.
			const int Radius = 40;
			const int CovenantPromptId = 121;

			int menu = memory.GetPromptedMenu();

			if (menu != run.Data)
			{
				run.Data = menu;

				if (menu == CovenantPromptId)
				{
					// At this point, the covenant prompt has appeared, but it's unknown to which covenant the prompt
					// applies (since all covenants use the same prompt).
					int index = ComputeClosestTarget(covenantLocations, Radius);
					int target = run.Target;

					// Way of White is the only covenant that can be joined from two locations. Conveniently, the first
					// two locations in the array can both be used for Way of White (since there's no covenant zero).
					if (index <= 1)
					{
						return target == (int)CovenantFlags.WayOfWhite;
					}

					// Covenant locations are ordered the same as their corresponding covenant ID (ranging from 1
					// through 9 inclusive).
					return index == target;
				}
			}

			return false;
		}

		private bool CheckCovenantJoin()
		{
			int covenant = (int)memory.GetCovenant();

			if (covenant != run.Data)
			{
				run.Data = covenant;

				return covenant == run.Target;
			}

			return false;
		}

		private bool ProcessEvent(int[] data)
		{
			bool isBell = data[0] <= 2;

			return isBell ? ProcessBell(data) : ProcessEnding(data);
		}

		private bool ProcessBell(int[] data)
		{
			bool rung = memory.CheckFlag(run.Id);

			if (rung && !run.Flag)
			{
				run.Flag = true;

				bool onRing = data[1] == 0;

				if (onRing)
				{
					return true;
				}

				PrepareWarp();
			}

			return false;
		}

		private bool ProcessEnding(int[] data)
		{
			int clearCount = memory.GetClearCount();

			if (clearCount == run.Data + 1)
			{
				run.Data++;

				// The player's X coordinate increases as you approach the exit (the exit is at roughly 421).
				bool isDarkLord = memory.GetPlayerX() > 415;
				bool isDarkLordTarget = data[0] == 5;

				return isDarkLord == isDarkLordTarget;
			}

			return false;
		}

		private bool ProcessFlag(int[] data)
		{
			int flag = data[0];

			if (!run.Flag && memory.CheckFlag(flag))
			{
				bool onWarp = data[1] == 1;

				// The alternative here is "On trigger" (i.e. split immediately when the flag is toggled to true).
				if (onWarp)
				{
					PrepareWarp();

					return false;
				}

				return true;
			}

			return false;
		}

		private bool ProcessItem(int[] data)
		{
			if (IsTargetItemSatisfied())
			{
				bool onWarp = data[5] == 1;

				// The alternative to "On warp" is "On acquisition".
				if (onWarp)
				{
					PrepareWarp();

					return false;
				}

				return true;
			}

			return false;
		}

		// This check is done from two places (processing item splits and verifying that an item wasn't dropped while
		// waiting for a warp).
		private bool IsTargetItemSatisfied()
		{
			int targetId = run.Id;

			// This double array felt like the easiest way to handle estus splits, even though literally every item
			// besides the estus flask will only use a single state array.
			ItemState[][] states = new ItemState[2][];
			states[0] = memory.GetItemStates(targetId, run.Data);

			if (isEstusSplit)
			{
				// For estus splits, the unfilled ID (at the target reinforement) is stored. Adding one brings you to
				// the filled ID for that same reinforcement level.
				states[1] = memory.GetItemStates(targetId + 1, run.Data);
			}

			return states.Any(a => a != null && a.Any(s => s.Satisfies(run.ItemTarget)));
		}

		private int ComputeClosestTarget(Vector3[] targets, int radius)
		{
			Vector3 playerPosition = memory.GetPlayerPosition();

			int closestIndex = -1;

			float closestDistance = float.MaxValue;
			float radiusSquared = radius * radius;

			for (int i = 0; i < targets.Length; i++)
			{
				float d = playerPosition.ComputeDistanceSquared(targets[i]);

				// Using distance squared prevents having to do a square root operation.
				if (d <= radiusSquared && d < closestDistance)
				{
					closestDistance = d;
					closestIndex = i;
				}
			}

			return closestIndex;
		}

		public void Dispose()
		{
		}
	}
}