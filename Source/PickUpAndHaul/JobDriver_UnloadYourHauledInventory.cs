namespace PickUpAndHaul;

public class JobDriver_UnloadYourHauledInventory : JobDriver
{
	private int _countToDrop = -1;
	private int _unloadDuration = 3;

	public override void ExposeData()
	{
		base.ExposeData();
		Scribe_Values.Look<int>(ref _countToDrop, "countToDrop", -1);
	}

	public override bool TryMakePreToilReservations(bool errorOnFailed) => job.playerForced || PawnUnloadChecker.CanUnloadNow(pawn);

	/// <summary>
	/// Find spot, reserve spot, pull thing out of inventory, go to spot, drop stuff, repeat.
	/// </summary>
	/// <returns></returns>
	public override IEnumerable<Toil> MakeNewToils()
	{
		if (ModCompatibilityCheck.ExtendedStorageIsActive)
		{
			_unloadDuration = 20;
		}

		AddFinishAction(condition =>
		{
			if (condition == JobCondition.Incompletable && pawn.carryTracker?.innerContainer is { Count: > 0 })
			{
				PawnUnloadChecker.NoteBlocked(pawn);
			}
		});

		var begin = Toils_General.Wait(_unloadDuration);
		yield return begin;

		var carriedThings = pawn.TryGetComp<CompHauledToInventory>().GetHashSet();
		yield return FindTargetOrDrop(carriedThings);
		yield return PullItemFromInventory(carriedThings, begin);

		var releaseReservation = ReleaseReservation();
		var carryToCell = Toils_Haul.CarryHauledThingToCell(TargetIndex.B);

		// Equivalent to if (TargetB.HasThing)
		yield return Toils_Jump.JumpIf(carryToCell, TargetIsCell);

		var carryToContainer = Toils_Haul.CarryHauledThingToContainer();
		yield return carryToContainer;
		yield return Toils_Haul.DepositHauledThingInContainer(TargetIndex.B, TargetIndex.None);
		yield return Toils_Haul.JumpToCarryToNextContainerIfPossible(carryToContainer, TargetIndex.B);
		// Equivalent to jumping out of the else block
		yield return Toils_Jump.Jump(releaseReservation);

		// Equivalent to else
		yield return carryToCell;
		yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.B, carryToCell, true);

		//If the original cell is full, PlaceHauledThingInCell will set a different TargetIndex resulting in errors on yield return Toils_Reserve.Release.
		//We still gotta release though, mostly because of Extended Storage.
		yield return releaseReservation;
		yield return Toils_Jump.Jump(begin);
	}

	private bool TargetIsCell() => !TargetB.HasThing;

	private Toil ReleaseReservation()
	{
		return new()
		{
			initAction = () =>
			{
				if (pawn.Map.reservationManager.ReservedBy(job.targetB, pawn, pawn.CurJob))
				{
					pawn.Map.reservationManager.Release(job.targetB, pawn, pawn.CurJob);
				}
			}
		};
	}

	private Toil PullItemFromInventory(HashSet<Thing> carriedThings, Toil wait)
	{
		return new()
		{
			initAction = () =>
			{
				var thing = job.GetTarget(TargetIndex.A).Thing;
				if (thing == null || !pawn.inventory.innerContainer.Contains(thing))
				{
					carriedThings.Remove(thing);
					pawn.jobs.curDriver.JumpToToil(wait);
					return;
				}

				ReturnCarriedThingToInventory(carriedThings);

				if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) || !thing.def.EverStorable(false))
				{
					Log.Message($"Pawn {pawn} incapable of hauling, dropping {thing}");
					pawn.inventory.innerContainer.TryDrop(thing, ThingPlaceMode.Near, thing.stackCount, out var dropped);
					carriedThings.Remove(thing);
					dropped?.SetForbidden(false, false);
					if (ModCompatibilityCheck.CombatExtendedIsActive)
					{
						CompatHelper.UpdateInventory(pawn);
					}
					EndJobWith(JobCondition.Succeeded);
					return;
				}

				pawn.inventory.innerContainer.TryTransferToContainer(thing, pawn.carryTracker.innerContainer, _countToDrop, out var carried);
				if (carried == null)
				{
					EndJobWith(JobCondition.Incompletable);
					return;
				}

				job.count = carried.stackCount;
				job.SetTarget(TargetIndex.A, carried);
				carriedThings.Remove(carried);
				if (!pawn.inventory.innerContainer.Contains(thing))
				{
					carriedThings.Remove(thing);
				}

				if (ModCompatibilityCheck.CombatExtendedIsActive)
				{
					CompatHelper.UpdateInventory(pawn);
				}

				carried.SetForbidden(false, false);
			}
		};
	}

	private void ReturnCarriedThingToInventory(HashSet<Thing> carriedThings)
	{
		var leftover = pawn.carryTracker.CarriedThing;
		if (leftover == null || leftover is Pawn)
		{
			return;
		}

		if (pawn.carryTracker.innerContainer.TryTransferToContainer(leftover, pawn.inventory.innerContainer, leftover.stackCount, out var back, false) > 0 && back != null)
		{
			carriedThings.Add(back);
		}
		else
		{
			pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
		}
	}

	private Toil FindTargetOrDrop(HashSet<Thing> carriedThings)
	{
		return new()
		{
			initAction = () =>
			{
				var unloadableThing = FirstUnloadableThing(pawn, carriedThings);

				if (unloadableThing.Count == 0)
				{
					EndJobWith(JobCondition.Succeeded);
					return;
				}

				var thing = unloadableThing.Thing;
				var currentPriority = StoragePriority.Unstored; // Currently in pawns inventory, so it's unstored
				if (StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, currentPriority,
						pawn.Faction, out var cell, out var destination))
				{
					job.SetTarget(TargetIndex.A, thing);
					if (cell == IntVec3.Invalid)
					{
						job.SetTarget(TargetIndex.B, destination as Thing);
					}
					else
					{
						job.SetTarget(TargetIndex.B, cell);
					}

					Log.Message($"{pawn} found destination {job.targetB} for thing {thing}");
					var count = thing.stackCount;
					if (cell == IntVec3.Invalid && destination is IHaulEnroute enroute)
					{
						count = Math.Min(count, enroute.GetSpaceRemainingWithEnroute(thing.def, pawn));
						if (count > 0)
						{
							pawn.Map.enrouteManager.AddEnroute(enroute, pawn, thing.def, count);
						}
					}
					else if (!pawn.Map.reservationManager.Reserve(pawn, job, job.targetB, errorOnFailed: false))
					{
						count = 0;
					}

					if (count <= 0)
					{
						Log.Message($"{pawn} failed reserving destination {job.targetB}, dropping {thing}");
						DropOrKeep(carriedThings, thing, JobCondition.Incompletable);
						return;
					}
					_countToDrop = count;
				}
				else
				{
					Log.Message($"Pawn {pawn} unable to find hauling destination, dropping {thing}");
					DropOrKeep(carriedThings, thing, JobCondition.Succeeded);
				}
			}
		};
	}

	private void DropOrKeep(HashSet<Thing> carriedThings, Thing thing, JobCondition condition)
	{
		if (pawn.Map.IsPlayerHome)
		{
			pawn.inventory.innerContainer.TryDrop(thing, ThingPlaceMode.Near, thing.stackCount, out _);
			carriedThings.Remove(thing);
			EndJobWith(condition);
		}
		else
		{
			EndJobWith(JobCondition.Incompletable);
		}
	}

	private static readonly List<Thing> staleThings = new();

	private static ThingCount FirstUnloadableThing(Pawn pawn, HashSet<Thing> carriedThings)
	{
		var innerPawnContainer = pawn.inventory.innerContainer;
		Thing best = null;
		var bestCategory = 0;
		string bestName = null;

		foreach (var thing in carriedThings)
		{
			if (thing == null || thing.Destroyed || !innerPawnContainer.Contains(thing))
			{
				staleThings.Add(thing);
				continue;
			}

			var category = thing.def.FirstThingCategory is { } firstCategory ? firstCategory.index + 1 : 0;
			var name = thing.def.defName;
			if (best == null || category < bestCategory || (category == bestCategory && Comparer<string>.Default.Compare(name, bestName) < 0))
			{
				best = thing;
				bestCategory = category;
				bestName = name;
			}
		}

		for (var i = 0; i < staleThings.Count; i++)
		{
			carriedThings.Remove(staleThings[i]);
		}
		staleThings.Clear();

		return best == null ? default : new ThingCount(best, best.stackCount);
	}
}
