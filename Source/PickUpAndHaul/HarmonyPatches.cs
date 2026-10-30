using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse.AI.Group;

namespace PickUpAndHaul;
[StaticConstructorOnStartup]
static class HarmonyPatches
{
	static HarmonyPatches()
	{
		var harmony = new Harmony("mehni.rimworld.pickupandhaul.main");
#if DEBUG
		Harmony.DEBUG = true;
#endif

		LongEventHandler.ExecuteWhenFinished(() => StackGapCompat.Apply(harmony));

		if (!ModCompatibilityCheck.CombatExtendedIsActive)
		{
			harmony.Patch(original: AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.GetMaxAllowedToPickUp), new[] { typeof(Pawn), typeof(ThingDef) }),
				prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(MaxAllowedToPickUpPrefix)));

			harmony.Patch(original: AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.CanPickUp)),
				prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(CanBeMadeToDropStuff)));
		}

		harmony.Patch(original: AccessTools.Method(typeof(JobGiver_DropUnusedInventory), nameof(JobGiver_DropUnusedInventory.TryGiveJob)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(DropUnusedInventory_PostFix)));

		harmony.Patch(original: AccessTools.Method(typeof(JobDriver_HaulToCell), nameof(JobDriver_HaulToCell.MakeNewToils)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(JobDriver_HaulToCell_PostFix)));

		harmony.Patch(original: AccessTools.Method(typeof(Pawn_InventoryTracker), nameof(Pawn_InventoryTracker.Notify_ItemRemoved)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Pawn_InventoryTracker_PostFix)));

		harmony.Patch(original: AccessTools.Method(typeof(JobGiver_DropUnusedInventory), nameof(JobGiver_DropUnusedInventory.Drop)),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Drop_Prefix)));

		harmony.Patch(original: AccessTools.Method(typeof(JobGiver_Idle), nameof(JobGiver_Idle.TryGiveJob)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(IdleJoy_Postfix)));

		harmony.Patch(original: AccessTools.Method(typeof(ITab_Pawn_Gear), nameof(ITab_Pawn_Gear.DrawThingRow)),
			transpiler: new HarmonyMethod(typeof(HarmonyPatches), nameof(GearTabHighlightTranspiler)));

		if (ModCompatibilityCheck.CombatExtendedIsActive && AccessTools.Method("CombatExtended.ITab_Inventory:DrawThingRowCE") is { } ceDrawThingRow)
		{
			harmony.Patch(ceDrawThingRow, transpiler: new HarmonyMethod(typeof(HarmonyPatches), nameof(GearTabHighlightTranspiler)));
		}

		CompatHelper.ApplyPatches(harmony);

		harmony.Patch(AccessTools.PropertySetter(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted)),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Drafted_Prefix)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Drafted_Postfix)));

		try
		{
			harmony.Patch(AccessTools.Method(typeof(ThingOwner<Thing>), nameof(ThingOwner<Thing>.TryAdd), new[] { typeof(Thing), typeof(bool) }),
				prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(InventoryTryAdd_Prefix)));
		}
		catch (Exception e)
		{
			Verse.Log.Warning($"Pick Up and Haul - Continued: could not keep own supplies apart from hauled items. {e.Message}");
		}

		RemoveDuplicateHauledComps();

		harmony.Patch(original: AccessTools.Method(typeof(WorkGiver_Haul), nameof(WorkGiver_Haul.ShouldSkip)),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(SkipCorpses_Prefix)));

		harmony.Patch(AccessTools.Method(typeof(JobGiver_Haul), nameof(JobGiver_Haul.TryGiveJob)),
			transpiler: new(typeof(HarmonyPatches), nameof(JobGiver_Haul_TryGiveJob_Transpiler)));

		Verse.Log.Message("Pick Up and Haul - Continued welcomes you to RimWorld with pointless logspam.");
	}

	private static void Drafted_Prefix(Pawn_DraftController __instance, out bool __state) => __state = __instance.Drafted;

	private static void Drafted_Postfix(Pawn_DraftController __instance, bool __state)
	{
		var pawn = __instance.pawn;
		if (__state == __instance.Drafted || pawn is not { Spawned: true } || pawn.inventory == null)
		{
			return;
		}

		if (!__instance.Drafted)
		{
			PawnUnloadChecker.QueueUnload(pawn, true);
			return;
		}

		if (!Settings.DropHauledWhenDrafted || !pawn.RaceProps.Humanlike)
		{
			return;
		}

		var hauled = pawn.GetComp<CompHauledToInventory>()?.GetHashSet();
		if (hauled == null || hauled.Count == 0)
		{
			return;
		}

		var inventory = pawn.inventory.innerContainer;
		foreach (var thing in hauled.ToList())
		{
			if (!thing.Destroyed && inventory.Contains(thing))
			{
				inventory.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, thing.stackCount, out var dropped);
				PawnUnloadChecker.MarkForHauling(dropped);
			}
		}
		hauled.RemoveWhere(thing => thing.Destroyed || !inventory.Contains(thing));

		if (ModCompatibilityCheck.CombatExtendedIsActive)
		{
			CompatHelper.UpdateInventory(pawn);
		}
	}

	private static bool InventoryTryAdd_Prefix(ThingOwner __instance, Thing item, ref bool canMergeWithExistingStacks, ref bool __result)
	{
		if (!canMergeWithExistingStacks || item == null || __instance.Owner is not Pawn_InventoryTracker tracker)
		{
			return true;
		}

		var hauled = tracker.pawn?.GetComp<CompHauledToInventory>();
		if (hauled == null || !hauled.HasAny || !StacksWithHauled(__instance, hauled, item))
		{
			return true;
		}

		canMergeWithExistingStacks = false;
		if (item.holdingOwner != null || __instance.Contains(item))
		{
			return true;
		}

		for (var i = 0; i < __instance.Count; i++)
		{
			var held = __instance.GetAt(i);
			if (hauled.Contains(held) || !held.CanStackWith(item))
			{
				continue;
			}

			var space = Mathf.Min(item.stackCount, held.def.stackLimit - held.stackCount);
			if (space <= 0)
			{
				continue;
			}

			var before = held.stackCount;
			held.TryAbsorbStack(item.SplitOff(space), true);
			if (held.stackCount > before)
			{
				__instance.NotifyAddedAndMergedWith(held, held.stackCount - before);
			}

			if (item.Destroyed || item.stackCount == 0)
			{
				__result = true;
				return false;
			}
		}
		return true;
	}

	private static bool StacksWithHauled(ThingOwner owner, CompHauledToInventory hauled, Thing item)
	{
		for (var i = 0; i < owner.Count; i++)
		{
			var held = owner.GetAt(i);
			if (hauled.Contains(held) && held.CanStackWith(item))
			{
				return true;
			}
		}
		return false;
	}

	private static void RemoveDuplicateHauledComps()
	{
		var defs = DefDatabase<ThingDef>.AllDefsListForReading;
		for (var i = 0; i < defs.Count; i++)
		{
			var comps = defs[i].comps;
			if (comps == null)
			{
				continue;
			}

			var found = false;
			for (var j = 0; j < comps.Count; j++)
			{
				if (comps[j].compClass != typeof(CompHauledToInventory))
				{
					continue;
				}

				if (found)
				{
					comps.RemoveAt(j--);
				}
				else
				{
					found = true;
				}
			}
		}
	}

	private static bool Drop_Prefix(Pawn pawn, Thing thing)
	{
		var takenToInventory = pawn.GetComp<CompHauledToInventory>();
		if (takenToInventory == null)
		{
			return true;
		}

		var carriedThing = takenToInventory.GetHashSet();
		return !carriedThing.Contains(thing);
	}

	private static void Pawn_InventoryTracker_PostFix(Pawn_InventoryTracker __instance, Thing item)
	{
		var takenToInventory = __instance.pawn?.GetComp<CompHauledToInventory>();
		if (takenToInventory == null)
		{
			return;
		}

		var carriedThing = takenToInventory.GetHashSet();
		if (carriedThing?.Count > 0)
		{
			carriedThing.Remove(item);
		}
	}

	private static void JobDriver_HaulToCell_PostFix(JobDriver_HaulToCell __instance)
	{
		var pawn = __instance.pawn;
		var takenToInventory = pawn?.GetComp<CompHauledToInventory>();
		if (takenToInventory == null)
		{
			return;
		}

		var carriedThing = takenToInventory.GetHashSet();

		if (__instance.job.haulMode == HaulMode.ToCellStorage
			&& pawn.Faction == Faction.OfPlayerSilentFail
			&& Settings.IsAllowedRace(pawn.RaceProps)
			&& (Settings.AllowCorpses || __instance.job.targetA.Thing is not Corpse)
			&& carriedThing != null
			&& carriedThing.Count != 0) //deliberate hauling job. Should unload.
		{
			PawnUnloadChecker.CheckIfPawnShouldUnloadInventory(pawn, true);
		}
	}

	public static void IdleJoy_Postfix(Pawn pawn)
	{
		if (pawn?.GetLord() == null)
		{
			PawnUnloadChecker.CheckIfPawnShouldUnloadInventory(pawn, true);
		}
	}

	public static void DropUnusedInventory_PostFix(Pawn pawn) => PawnUnloadChecker.CheckIfPawnShouldUnloadInventory(pawn);

	public static bool MaxAllowedToPickUpPrefix(Pawn pawn, ref int __result)
	{
		__result = int.MaxValue;
		return pawn.IsQuestLodger();
	}

	public static bool CanBeMadeToDropStuff(Pawn pawn, ref bool __result)
	{
		__result = !pawn.IsQuestLodger();
		return false;
	}

	public static bool SkipCorpses_Prefix(WorkGiver_Haul __instance, ref bool __result, Pawn pawn)
	{
		if (__instance is not WorkGiver_HaulCorpses)
		{
			return true;
		}

		if ((Settings.AllowCorpses && !WorkGiver_HaulToInventory.SkipsPawn(pawn)) //Don't use the vanilla HaulCorpses WorkGiver if PUAH is allowed to haul those
			|| pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).Count < 1) //...or if there are no corpses to begin with. Indeed Tynan did not foresee this situation
		{
			__result = true;
			return false;
		}

		return true;
	}

	/// <summary>
	/// For animal hauling
	/// </summary>
	public static IEnumerable<CodeInstruction> JobGiver_Haul_TryGiveJob_Transpiler(IEnumerable<CodeInstruction> instructions)
		=> instructions.MethodReplacer(HaulAIUtility.HaulToStorageJob, HaulToStorageJobByRace);

	public static Job HaulToStorageJobByRace(Pawn p, Thing t, bool forced) => Settings.IsAllowedRace(p.RaceProps) && !CompatHelper.CeUnsupported ? HaulToInventoryJob(p, t, forced) : HaulAIUtility.HaulToStorageJob(p, t, forced);
	private static Func<Pawn, Thing, bool, Job> HaulToInventoryJob => _haulToInventoryJob ??= new(((WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("HaulToInventory").Worker).JobOnThing);
	private static Func<Pawn, Thing, bool, Job> _haulToInventoryJob;

	//ITab_Pawn_Gear
	//private void DrawThingRow(ref float y, float width, Thing thing, bool inventory = false)
	public static IEnumerable<CodeInstruction> GearTabHighlightTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase method)
	{
		var ColorWhite = AccessTools.PropertyGetter(typeof(Color), nameof(Color.white));

		var done = false;
		foreach (var i in instructions)
		{
			//// Color color = flag ? Color.grey : Color.white;
			if (!done && i.Calls(ColorWhite))
			{
				yield return FishTranspiler.This;
				yield return FishTranspiler.CallPropertyGetter(typeof(ITab_Pawn_Gear), nameof(ITab_Pawn_Gear.SelPawnForGear));
				yield return FishTranspiler.Argument(method, "thing");
				yield return FishTranspiler.Call(GetColorForHauled);
				done = true;
			}
			else
			{
				yield return i;
			}
		}

		if (!done)
		{
			Verse.Log.Warning("Pick Up And Haul failed to patch ITab_Pawn_Gear.DrawThingRow. This is only used for coloring and totally harmless, but you might wanna know anyway");
		}
	}

	private static Color GetColorForHauled(Pawn pawn, Thing thing)
		=> pawn.GetComp<CompHauledToInventory>()?.GetHashSet().Contains(thing) ?? false
		? Color.Lerp(Color.grey, Color.red, 0.5f)
		: Color.white;
}
