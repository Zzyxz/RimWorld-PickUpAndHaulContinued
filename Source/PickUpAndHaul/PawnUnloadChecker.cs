using Verse.AI.Group;

namespace PickUpAndHaul;
public class PawnUnloadChecker
{
	private const int BlockedRetryTicks = 750;

	private static readonly Dictionary<int, int> blockedUntil = new();

	public static void CheckIfPawnShouldUnloadInventory(Pawn pawn, bool forced = false) => QueueUnload(pawn, true);

	public static bool CanUnloadNow(Pawn pawn)
		=> !pawn.Downed
		&& !pawn.Dead
		&& pawn.GetLord() == null
		&& pawn.CurJobDef != JobDefOf.Deathrest
		&& pawn.needs?.energy is not { selfShutdown: true }
		&& (!pawn.RaceProps.IsMechanoid || pawn.GetOverseer() == null || pawn.GetMechWorkMode() == MechWorkModeDefOf.Work);

	public static void NoteBlocked(Pawn pawn) => blockedUntil[pawn.thingIDNumber] = Find.TickManager.TicksGame + BlockedRetryTicks;

	private static bool IsBlocked(Pawn pawn)
	{
		if (!blockedUntil.TryGetValue(pawn.thingIDNumber, out var until))
		{
			return false;
		}

		if (Find.TickManager.TicksGame < until)
		{
			return true;
		}

		blockedUntil.Remove(pawn.thingIDNumber);
		return false;
	}

	public static bool QueueUnload(Pawn pawn, bool first, bool ignoreCurrentJob = false)
	{
		var itemsTakenToInventory = pawn?.GetComp<CompHauledToInventory>();
		if (itemsTakenToInventory == null)
		{
			return false;
		}

		if (pawn.Faction != Faction.OfPlayerSilentFail || !Settings.IsAllowedRace(pawn.RaceProps)
			|| pawn.Map is not { IsPlayerHome: true } || pawn.jobs == null
			|| pawn.inventory?.innerContainer is not { Count: > 0 } inventoryContainer)
		{
			return false;
		}

		var carriedThing = itemsTakenToInventory.GetHashSet();
		if (carriedThing.Count == 0)
		{
			return false;
		}

		carriedThing.RemoveWhere(t => !inventoryContainer.Contains(t));
		if (carriedThing.Count == 0 || !CanUnloadNow(pawn) || IsBlocked(pawn) || UnloadPending(pawn, ignoreCurrentJob))
		{
			return false;
		}

		var job = JobMaker.MakeJob(PickUpAndHaulJobDefOf.UnloadYourHauledInventory, pawn);
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
			if (queuedDef == PickUpAndHaulJobDefOf.UnloadYourHauledInventory || queuedDef == PickUpAndHaulJobDefOf.HaulToInventory)
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
