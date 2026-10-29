using System.Linq;

namespace PickUpAndHaul;
public class JobDriver_HaulToInventory : JobDriver
{
	public override bool TryMakePreToilReservations(bool errorOnFailed)
	{
		Log.Message($"{pawn} starting HaulToInventory job: {job.targetQueueA.ToStringSafeEnumerable()}:{job.countQueue.ToStringSafeEnumerable()}");
		DropUnreservableQueuedTargets();
		if (!pawn.Reserve(job.targetQueueA[0], job, errorOnFailed: errorOnFailed))
		{
			return false;
		}

		if (!IsEnrouteContainer(job.targetB) && !pawn.Reserve(job.targetB, job, errorOnFailed: errorOnFailed))
		{
			return false;
		}

		pawn.ReserveAsManyAsPossible(job.targetQueueA, job);
		for (var i = 0; i < job.targetQueueB.Count; i++)
		{
			var target = job.targetQueueB[i];
			if (!IsEnrouteContainer(target) && pawn.CanReserve(target))
			{
				pawn.Reserve(target, job, errorOnFailed: false);
			}
		}
		return true;
	}

	private void DropUnreservableQueuedTargets()
	{
		var targets = job.targetQueueA;
		var counts = job.countQueue;
		var aligned = counts != null && counts.Count == targets.Count;
		for (var i = targets.Count - 1; i >= 1; i--)
		{
			if (!pawn.CanReserve(targets[i]))
			{
				targets.RemoveAt(i);
				if (aligned)
				{
					counts.RemoveAt(i);
				}
			}
		}
	}

	private static bool IsEnrouteContainer(LocalTargetInfo target) => target.HasThing && target.Thing is IHaulEnroute;

	private void OnFinished(JobCondition condition)
	{
		if (condition == JobCondition.InterruptForced || !pawn.Spawned || pawn.Drafted || pawn.Downed || pawn.Dead)
		{
			return;
		}

		PawnUnloadChecker.QueueUnload(pawn, false, true);
	}

	//get next, goto, take, check for more. Branches off to "all over the place"
	public override IEnumerable<Toil> MakeNewToils()
	{
		var takenToInventory = pawn.TryGetComp<CompHauledToInventory>();
		AddFinishAction(OnFinished);

		var wait = Toils_General.Wait(2);

		var nextTarget = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A, false); //also does count
		yield return nextTarget;

		yield return CheckForOverencumberedForCombatExtended();

		var gotoThing = new Toil
		{
			initAction = () => pawn.pather.StartPath(TargetThingA, PathEndMode.ClosestTouch),
			defaultCompleteMode = ToilCompleteMode.PatherArrival
		};
		gotoThing.FailOnDespawnedNullOrForbidden(TargetIndex.A);
		yield return gotoThing;

		var takeThing = new Toil
		{
			initAction = () =>
			{
				var actor = pawn;
				var thing = actor.CurJob.GetTarget(TargetIndex.A).Thing;
				if (thing == null || Toils_Haul.ErrorCheckForCarry(actor, thing))
				{
					return;
				}

				if (!actor.HasReserved(thing, job))
				{
					if (!actor.CanReserve(thing) || !actor.Reserve(thing, job, errorOnFailed: false))
					{
						return;
					}
				}

				//get max we can pick up
				var countToPickUp = Mathf.Min(job.count, WorkGiver_HaulToInventory.CountToHaulUntilFull(actor, thing), thing.stackCount);
				Log.Message($"{actor} is hauling to inventory {thing}:{countToPickUp}");

				if (ModCompatibilityCheck.CombatExtendedIsActive)
				{
					countToPickUp = Mathf.Min(countToPickUp, CompatHelper.CanFitInInventory(pawn, thing));
				}

				if (countToPickUp > 0)
				{
					var splitThing = thing.SplitOff(countToPickUp);
					StoreInInventory(actor, takenToInventory, splitThing);

					if (ModCompatibilityCheck.CombatExtendedIsActive)
					{
						CompatHelper.UpdateInventory(pawn);
					}
				}

				//thing still remains, so queue up hauling if we can + end the current job (smooth/instant transition)
				//This will technically release the reservations in the queue, but what can you do
				if (thing.Spawned)
				{
					var haul = HaulAIUtility.HaulToStorageJob(actor, thing, actor.CurJob.playerForced);
					TryEnqueueFirst(actor, haul);
					actor.jobs.curDriver.JumpToToil(wait);
				}
			}
		};
		yield return takeThing;
		yield return Toils_Jump.JumpIf(nextTarget, () => !job.targetQueueA.NullOrEmpty());

		//Find more to haul, in case things spawned while this was in progess
		yield return new Toil
		{
			initAction = () =>
			{
				var haulMoreJob = FindMoreWork(pawn);

				//WorkGiver_HaulToInventory found more work nearby
				if (haulMoreJob != null)
				{
					Log.Message($"{pawn} hauling again : {haulMoreJob.targetA}");
					if (TryEnqueueFirst(pawn, haulMoreJob))
					{
						EndJobWith(JobCondition.Succeeded);
					}
				}
			}
		};

		//maintain cell reservations on the trip back
		//TODO: do that when we carry things
		//I guess that means TODO: implement carrying the rest of the items in this job instead of falling back on HaulToStorageJob
		yield return TargetB.HasThing ? Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch)
			: Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.ClosestTouch);

		yield return new Toil //Queue next job
		{
			initAction = () =>
			{
				var actor = pawn;
				var curJob = actor.jobs.curJob;
				var storeCell = curJob.targetB;

				var unloadJob = JobMaker.MakeJob(PickUpAndHaulJobDefOf.UnloadYourHauledInventory, storeCell);
				if (unloadJob.TryMakePreToilReservations(actor, false))
				{
					actor.jobs.jobQueue.EnqueueFirst(unloadJob, JobTag.Misc);
					EndJobWith(JobCondition.Succeeded);
					//This will technically release the cell reservations in the queue, but what can you do
				}
			}
		};
		yield return wait;
	}

	private static void StoreInInventory(Pawn actor, CompHauledToInventory takenToInventory, Thing splitThing)
	{
		var inventory = actor.inventory.GetDirectlyHeldThings();
		foreach (var hauled in takenToInventory.GetHashSet())
		{
			if (hauled != splitThing && !hauled.Destroyed && inventory.Contains(hauled) && hauled.CanStackWith(splitThing)
				&& hauled.TryAbsorbStack(splitThing, true))
			{
				return;
			}
		}

		if (inventory.TryAdd(splitThing, false))
		{
			takenToInventory.RegisterHauledItem(splitThing);
		}
		else
		{
			GenPlace.TryPlaceThing(splitThing, actor.Position, actor.Map, ThingPlaceMode.Near);
		}
	}

	private static bool TryEnqueueFirst(Pawn actor, Job job)
	{
		if (job == null)
		{
			return false;
		}

		if (job.TryMakePreToilReservations(actor, false))
		{
			actor.jobs.jobQueue.EnqueueFirst(job, JobTag.Misc);
			return true;
		}

		actor.ClearReservationsForJob(job);
		return false;
	}

	private const int FindMoreAttempts = 8;

	private static WorkGiver_HaulToInventory haulMoreWorker;

	private static WorkGiver_HaulToInventory HaulMoreWorker
		=> haulMoreWorker ??= DefDatabase<WorkGiverDef>.AllDefsListForReading.First(wg => wg.Worker is WorkGiver_HaulToInventory).Worker as WorkGiver_HaulToInventory;

	private const int FindMoreRangeSquared = 12 * 12;

	private static Thing[] nearbyThings = new Thing[16];
	private static int[] nearbyDistances = new int[16];

	private static Job FindMoreWork(Pawn pawn)
	{
		var map = pawn.Map;
		var center = pawn.Position;
		var haulables = map.listerHaulables.ThingsPotentiallyNeedingHauling();
		var count = 0;
		foreach (var thing in haulables)
		{
			if (!thing.Spawned)
			{
				continue;
			}

			var distance = (center - thing.Position).LengthHorizontalSquared;
			if (distance > FindMoreRangeSquared)
			{
				continue;
			}

			if (count == nearbyThings.Length)
			{
				Array.Resize(ref nearbyThings, count * 2);
				Array.Resize(ref nearbyDistances, count * 2);
			}
			nearbyThings[count] = thing;
			nearbyDistances[count] = distance;
			count++;
		}

		Job found = null;
		if (count > 0)
		{
			Array.Sort(nearbyDistances, nearbyThings, 0, count);
			var traverseParms = TraverseParms.For(pawn);
			var attempts = 0;
			for (var i = 0; i < count && attempts < FindMoreAttempts && found == null; i++)
			{
				var candidate = nearbyThings[i];
				if (!candidate.Spawned
					|| !map.reachability.CanReach(center, candidate, PathEndMode.ClosestTouch, traverseParms)
					|| !WorkGiver_HaulToInventory.GoodThingToHaul(candidate, pawn)
					|| !WorkGiver_HaulToInventory.CanHaulFast(pawn, candidate, false, false)
					|| !StoreUtility.TryFindBestBetterStorageFor(candidate, pawn, map, StoreUtility.CurrentStoragePriorityOf(candidate), pawn.Faction, out _, out _, false))
				{
					continue;
				}

				attempts++;
				found = HaulMoreWorker.JobOnThing(pawn, candidate);
			}
		}

		Array.Clear(nearbyThings, 0, count);
		return found;
	}

	/// <summary>
	/// the workgiver checks for encumbered, this is purely extra for CE
	/// </summary>
	/// <returns></returns>
	public Toil CheckForOverencumberedForCombatExtended()
	{
		var toil = new Toil();

		if (!ModCompatibilityCheck.CombatExtendedIsActive)
		{
			return toil;
		}

		toil.initAction = () =>
		{
			var actor = toil.actor;
			var curJob = actor.jobs.curJob;
			var nextThing = curJob.targetA.Thing;

			var ceOverweight = CompatHelper.CeFull(pawn);

			var haulCapacity = WorkGiver_HaulToInventory.HaulCapacity(actor);
			var haulLoad = haulCapacity > 0f ? MassUtility.GearAndInventoryMass(actor) / haulCapacity : 1f;
			if (!(haulLoad <= 0.9f && !ceOverweight))
			{
				var haul = HaulAIUtility.HaulToStorageJob(actor, nextThing, curJob.playerForced);
				if (TryEnqueueFirst(actor, haul))
				{
					//note that HaulToStorageJob etc doesn't do opportunistic duplicate hauling for items in valid storage. REEEE
					EndJobWith(JobCondition.Succeeded);
				}
			}
		};

		return toil;
	}
}
