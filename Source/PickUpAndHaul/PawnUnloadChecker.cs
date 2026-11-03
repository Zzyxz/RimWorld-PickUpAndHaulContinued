using Verse.AI.Group;

namespace PickUpAndHaul;
public class PawnUnloadChecker
{
	private const int BlockedRetryTicks = 750;

	private static readonly Dictionary<int, int> blockedUntil = new();
	private static Game blockedGame;

	private static Dictionary<int, int> BlockedUntil
	{
		get
		{
			if (blockedGame != Current.Game)
			{
				blockedUntil.Clear();
				blockedGame = Current.Game;
			}
			return blockedUntil;
		}
	}

	public static void CheckIfPawnShouldUnloadInventory(Pawn pawn, bool forced = false) => QueueUnload(pawn, true);

	public static bool CanUnloadNow(Pawn pawn)
		=> !pawn.Downed
		&& !pawn.Dead
		&& pawn.GetLord() == null
		&& pawn.CurJobDef != JobDefOf.Deathrest
		&& pawn.needs?.energy is not { selfShutdown: true }
		&& (!pawn.RaceProps.IsMechanoid || pawn.GetOverseer() == null || pawn.GetMechWorkMode() == MechWorkModeDefOf.Work);

	public static void NoteBlocked(Pawn pawn) => BlockedUntil[pawn.thingIDNumber] = Find.TickManager.TicksGame + BlockedRetryTicks;

	public static void MarkForHauling(Thing thing)
	{
		if (thing is not { Spawned: true } || thing.def.alwaysHaulable || !thing.def.EverHaulable || thing.IsInAnyStorage()
			|| thing.IsForbidden(Faction.OfPlayer))
		{
			return;
		}

		var designationManager = thing.Map.designationManager;
		if (designationManager.DesignationOn(thing, DesignationDefOf.Haul) == null)
		{
			designationManager.AddDesignation(new Designation(thing, DesignationDefOf.Haul));
		}
	}

	private static bool IsBlocked(Pawn pawn)
	{
		var blocked = BlockedUntil;
		if (!blocked.TryGetValue(pawn.thingIDNumber, out var until))
		{
			return false;
		}

		if (Find.TickManager.TicksGame < until)
		{
			return true;
		}

		blocked.Remove(pawn.thingIDNumber);
		return false;
	}

	public static bool QueueUnload(Pawn pawn, bool first, bool ignoreCurrentJob = false)
	{
		var job = TryMakeUnloadJob(pawn, ignoreCurrentJob);
		if (job == null)
		{
			return false;
		}

		if (first)
		{
			pawn.jobs.jobQueue.EnqueueFirst(job, JobTag.Misc);
		}
		else
		{
			pawn.jobs.jobQueue.EnqueueLast(job, JobTag.Misc);
		}
		return true;
	}

	internal static Job TryMakeUnloadJob(Pawn pawn, bool ignoreCurrentJob = false)
	{
		var itemsTakenToInventory = pawn?.GetComp<CompHauledToInventory>();
		if (itemsTakenToInventory == null)
		{
			return null;
		}

		if (pawn.Faction != Faction.OfPlayerSilentFail || !Settings.IsAllowedRace(pawn.RaceProps)
			|| pawn.Map is not { IsPlayerHome: true } || pawn.jobs == null
			|| pawn.inventory?.innerContainer is not { Count: > 0 } inventoryContainer)
		{
			return null;
		}

		var carriedThing = itemsTakenToInventory.GetHashSet();
		if (carriedThing.Count == 0)
		{
			return null;
		}

		RemoveMissing(carriedThing, inventoryContainer);
		if (carriedThing.Count == 0 || !CanUnloadNow(pawn) || IsBlocked(pawn) || UnloadPending(pawn, ignoreCurrentJob))
		{
			return null;
		}

		return JobMaker.MakeJob(PickUpAndHaulJobDefOf.UnloadYourHauledInventory, pawn);
	}

	private static void RemoveMissing(HashSet<Thing> carriedThing, ThingOwner inventoryContainer)
		=> carriedThing.RemoveWhere(t => !inventoryContainer.Contains(t));

	private static bool UnloadPending(Pawn pawn, bool ignoreCurrentJob)
	{
		var curJobDef = ignoreCurrentJob ? null : pawn.CurJobDef;
		if (curJobDef != null && (curJobDef == PickUpAndHaulJobDefOf.UnloadYourHauledInventory || curJobDef == PickUpAndHaulJobDefOf.HaulToInventory))
		{
			return true;
		}

		var queue = pawn.jobs.jobQueue;
		for (var i = 0; i < queue.Count; i++)
		{
			var queuedDef = queue[i].job?.def;
			if (queuedDef == PickUpAndHaulJobDefOf.UnloadYourHauledInventory
				|| (queuedDef == PickUpAndHaulJobDefOf.HaulToInventory && !ModCompatibilityCheck.CartModIsActive))
			{
				return true;
			}
		}
		return false;
	}
}

[DefOf]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Has to match defName")]
public static class PickUpAndHaulJobDefOf
{
	public static JobDef UnloadYourHauledInventory;
	public static JobDef HaulToInventory;
}
