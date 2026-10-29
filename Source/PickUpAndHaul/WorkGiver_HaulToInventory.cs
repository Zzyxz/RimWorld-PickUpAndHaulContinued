using System.Linq;

namespace PickUpAndHaul;
public class WorkGiver_HaulToInventory : WorkGiver_HaulGeneral
{
	//Thanks to AlexTD for the more dynamic search range
	//And queueing
	//And optimizing
	private const float SEARCH_FOR_OTHERS_RANGE_FRACTION = 0.5f;

	public override bool ShouldSkip(Pawn pawn, bool forced = false)
		=> base.ShouldSkip(pawn, forced)
		|| SkipsPawn(pawn);

	public static bool SkipsPawn(Pawn pawn)
		=> pawn.Faction != Faction.OfPlayerSilentFail
		|| !Settings.IsAllowedRace(pawn.RaceProps)
		|| pawn.GetComp<CompHauledToInventory>() == null
		|| pawn.IsQuestLodger()
		|| CompatHelper.CeUnsupported
		|| OverAllowedGearCapacity(pawn);

	public static float HaulCapacity(Pawn pawn) => MassUtility.Capacity(pawn) * Settings.HaulCarryFactor;

	public static float FreeHaulMass(Pawn pawn) => Math.Max(0f, HaulCapacity(pawn) - MassUtility.GearAndInventoryMass(pawn));

	public static int CountToHaulUntilFull(Pawn pawn, Thing thing)
	{
		var mass = thing.GetStatValue(StatDefOf.Mass);
		return mass <= 0f ? thing.stackCount : Mathf.FloorToInt(FreeHaulMass(pawn) / mass);
	}

	public static bool IsCorpseBeingEaten(Thing t, Map map)
		=> t is Corpse
		&& map.physicalInteractionReservationManager.FirstReserverOf(t) is { } reserver
		&& reserver.RaceProps.Animal
		&& reserver.Faction != Faction.OfPlayer;

	public static bool GoodThingToHaul(Thing t, Pawn pawn)
		=> OkThingToHaul(t, pawn)
		&& IsNotCorpseOrAllowed(t)
		&& !t.IsInValidBestStorage();

	public static bool OkThingToHaul(Thing t, Pawn pawn)
		=> t.Spawned
		&& pawn.CanReserve(t)
		&& !t.IsForbidden(pawn);

	public static bool IsNotCorpseOrAllowed(Thing t) => Settings.AllowCorpses || t is not Corpse;

	public static bool CanHaulFast(Pawn pawn, Thing thing, bool forced, bool checkReachability)
#if RW17
		=> AchtungCompat.UsesAchtungCheck(pawn, forced)
			? HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced)
			: HaulAIUtility.PawnCanAutomaticallyHaulFast_NewTemp(pawn, thing, forced, checkReachability);
#else
		=> HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced);
#endif

	public static bool HasOtherDesignation(Thing t, DesignationManager designationManager)
	{
		var designations = designationManager.AllDesignationsOn(t);
		for (var i = 0; i < designations.Count; i++)
		{
			var def = designations[i].def;
			if (def != DesignationDefOf.Haul && def != PickUpAndHaulDesignationDefOf.haulUrgently)
			{
				return true;
			}
		}
		return false;
	}

	public static bool IsHaulUrgently(Thing t, DesignationManager designationManager)
		=> PickUpAndHaulDesignationDefOf.haulUrgently != null && designationManager.DesignationOn(t, PickUpAndHaulDesignationDefOf.haulUrgently) != null;

	private static int[] distanceKeys = new int[0];

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		var source = pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling();
		var things = new Thing[source.Count];
		source.CopyTo(things, 0);
		if (distanceKeys.Length < things.Length)
		{
			distanceKeys = new int[things.Length];
		}

		var rootCell = pawn.Position;
		for (var i = 0; i < things.Length; i++)
		{
			distanceKeys[i] = (things[i].Position - rootCell).LengthHorizontalSquared;
		}
		Array.Sort(distanceKeys, things, 0, things.Length);
		return things;
	}

	public override bool HasJobOnThing(Pawn pawn, Thing thing, bool forced = false)
		=> OkThingToHaul(thing, pawn)
		&& IsNotCorpseOrAllowed(thing)
		&& CanHaulFast(pawn, thing, forced, false)
		&& StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out _, out _, false);

	//bulky gear (power armor + minigun) so don't bother.
	public static bool OverAllowedGearCapacity(Pawn pawn) => MassUtility.GearMass(pawn) / MassUtility.Capacity(pawn) >= Settings.MaximumOccupiedCapacityToConsiderHauling;

	private static List<Thing> HaulablesBuffer { get; } = new();

	//pick up stuff until you can't anymore,
	//while you're up and about, pick up something and haul it
	//before you go out, empty your pockets
	public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		if (HaulAIUtility.IsInHaulableInventory(thing) || CompatHelper.CeUnsupported)
		{
			return HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced) ? HaulAIUtility.HaulToStorageJob(pawn, thing, forced) : null;
		}

		if (!OkThingToHaul(thing, pawn) || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced) || IsCorpseBeingEaten(thing, pawn.Map))
		{
			return null;
		}

		var map = pawn.Map;
		var designationManager = map.designationManager;

		if (OverAllowedGearCapacity(pawn)
			|| pawn.GetComp<CompHauledToInventory>() is null // Misc. Robots compatibility
															 // See https://github.com/catgirlfighter/RimWorld_CommonSense/blob/master/Source/CommonSense11/CommonSense/OpportunisticTasks.cs#L129-L140
			|| !IsNotCorpseOrAllowed(thing) //This WorkGiver gets hijacked by AllowTool and expects us to urgently haul corpses.
			|| FreeHaulMass(pawn) < thing.GetStatValue(StatDefOf.Mass) //https://github.com/Mehni/PickUpAndHaul/pull/18
			|| HasOtherDesignation(thing, designationManager)
			|| (CompatHelper.IsActive && (CompatHelper.CeOverweight(pawn) || CompatHelper.CanFitInInventory(pawn, thing) <= 0)))
		{
			return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
		}

		var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
		ThingOwner nonSlotGroupThingOwner = null;
		StoreTarget storeTarget;
		if (StoreUtility.TryFindBestBetterStorageFor(thing, pawn, map, currentPriority, pawn.Faction, out var targetCell, out var haulDestination, true))
		{
			if (haulDestination is ISlotGroupParent)
			{
				//since we've gone through all the effort of getting the loc, might as well use it.
				//Don't multi-haul food to hoppers.
				if (HaulToHopperJob(thing, targetCell, map) || StackGapCompat.HasRuleAt(targetCell, map))
				{
					return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
				}
				else
				{
					storeTarget = new(targetCell);
				}
			}
			else if (haulDestination is Thing destinationAsThing && (nonSlotGroupThingOwner = destinationAsThing.TryGetInnerInteractableThingOwner()) != null)
			{
				storeTarget = new(destinationAsThing);
			}
			else
			{
				Verse.Log.Error("Don't know how to handle HaulToStorageJob for storage " + haulDestination.ToStringSafe() + ". thing=" + thing.ToStringSafe());
				return null;
			}
		}
		else
		{
			JobFailReason.Is("NoEmptyPlaceLower".Translate());
			return null;
		}

		//credit to Dingo
		var capacityStoreCell
			= storeTarget.container is null ? CapacityAt(thing, storeTarget.cell, map)
			: nonSlotGroupThingOwner.GetCountCanAccept(thing);

		if (capacityStoreCell == 0)
		{
			return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
		}

		var job = JobMaker.MakeJob(PickUpAndHaulJobDefOf.HaulToInventory, null, storeTarget);   //Things will be in queues
		Log.Message($"-------------------------------------------------------------------");
		Log.Message($"------------------------------------------------------------------");//different size so the log doesn't count it 2x
		Log.Message($"{pawn} job found to haul: {thing} to {storeTarget}:{capacityStoreCell}, looking for more now");

		var freeMass = FreeHaulMass(pawn);
		var freeBulk = CompatHelper.AvailableBulk(pawn);
		job.targetQueueA = new List<LocalTargetInfo>(); //more things
		job.targetQueueB = new List<LocalTargetInfo>(); //more storage; keep in mind the job doesn't use it, but reserve it so you don't over-haul
		job.countQueue = new List<int>();//thing counts

		var distanceToHaul = (storeTarget.Position - thing.Position).LengthHorizontal * SEARCH_FOR_OTHERS_RANGE_FRACTION;
		var distanceToSearchMore = Math.Max(12f, distanceToHaul);

		//Find extra things than can be hauled to inventory, queue to reserve them
		var isUrgent = ModCompatibilityCheck.AllowToolIsActive && IsHaulUrgently(thing, designationManager);

		var haulables = HaulablesBuffer;
		haulables.Clear();
		haulables.AddRange(map.listerHaulables.ThingsPotentiallyNeedingHauling());

		var nextThing = thing;
		var lastThing = thing;

		var storeCellCapacity = new Dictionary<StoreTarget, CellAllocation>()
		{
			[storeTarget] = new(nextThing, capacityStoreCell)
		};
		//skipTargets = new() { storeTarget };
		skipCells = new();
		skipThings = new();
		if (storeTarget.container != null)
		{
			skipThings.Add(storeTarget.container);
		}
		else
		{
			skipCells.Add(storeTarget.cell);
		}

		bool Validator(Thing t)
			=> t != thing
			&& (!isUrgent || IsHaulUrgently(t, designationManager))
			&& GoodThingToHaul(t, pawn)
			&& !HasOtherDesignation(t, designationManager)
			&& !IsCorpseBeingEaten(t, map)
			&& CanHaulFast(pawn, t, false, false); //forced is false, may differ from first thing

		do
		{
			var queued = job.countQueue.Count;
			if (AllocateThingAtCell(storeCellCapacity, pawn, nextThing, job))
			{
				lastThing = nextThing;
			}

			if (job.countQueue.Count > queued && !FitInInventory(nextThing, job, ref freeMass, ref freeBulk))
			{
				// We are now out of inventory space - and should bail right away.
				break;
			}
		}
		while ((nextThing = GetClosestAndRemove(lastThing.Position, map, haulables, PathEndMode.ClosestTouch,
			TraverseParms.For(pawn), distanceToSearchMore, Validator)) != null);

		haulables.Clear();
		skipCells = null;
		skipThings = null;
		return job;
	}

	private static bool FitInInventory(Thing thing, Job job, ref float freeMass, ref float freeBulk)
	{
		var last = job.countQueue.Count - 1;
		var count = job.countQueue[last];
		var mass = thing.GetStatValue(StatDefOf.Mass);
		var bulk = CompatHelper.BulkPerItem(thing);

		var fit = count;
		if (mass > 0f)
		{
			fit = Math.Min(fit, (int)Math.Max(0f, freeMass / mass));
		}
		if (bulk > 0f && !float.IsPositiveInfinity(freeBulk))
		{
			fit = Math.Min(fit, (int)Math.Max(0f, freeBulk / bulk));
		}

		if (fit >= count)
		{
			freeMass -= count * mass;
			freeBulk -= count * bulk;
			return true;
		}

		if (fit <= 0 && job.targetQueueA.Count > 1)
		{
			job.countQueue.RemoveAt(last);
			job.targetQueueA.RemoveAt(job.targetQueueA.Count - 1);
		}
		else
		{
			job.countQueue[last] = Math.Max(1, fit);
		}
		Log.Message($"Inventory allocated, will carry {thing}:{fit}");
		return false;
	}

	private static bool HaulToHopperJob(Thing thing, IntVec3 targetCell, Map map)
	{
		if (thing.def.IsNutritionGivingIngestible
			&& thing.def.ingestible.preferability is FoodPreferability.RawBad or FoodPreferability.RawTasty)
		{
			var thingList = targetCell.GetThingList(map);
			for (var i = 0; i < thingList.Count; i++)
			{
				if (thingList[i].def == ThingDefOf.Hopper)
				{
					return true;
				}
			}
		}
		return false;
	}

	public struct StoreTarget : IEquatable<StoreTarget>
	{
		public IntVec3 cell;
		public Thing container;
		public IntVec3 Position => container?.Position ?? cell;

		public StoreTarget(IntVec3 cell)
		{
			this.cell = cell;
			container = null;
		}
		public StoreTarget(Thing container)
		{
			cell = default;
			this.container = container;
		}

		public bool Equals(StoreTarget other) => container is null ? other.container is null && cell == other.cell : container == other.container;
		public override int GetHashCode() => container?.GetHashCode() ?? cell.GetHashCode();
		public override string ToString() => container?.ToString() ?? cell.ToString();
		public override bool Equals(object obj) => obj is StoreTarget target ? Equals(target) : obj is Thing thing ? container == thing : obj is IntVec3 intVec && cell == intVec;
		public static bool operator ==(StoreTarget left, StoreTarget right) => left.Equals(right);
		public static bool operator !=(StoreTarget left, StoreTarget right) => !left.Equals(right);
		public static implicit operator LocalTargetInfo(StoreTarget target) => target.container != null ? target.container : target.cell;
	}

	public static Thing GetClosestAndRemove(IntVec3 center, Map map, List<Thing> searchSet, PathEndMode peMode, TraverseParms traverseParams, float maxDistance = 9999f, Predicate<Thing> validator = null)
	{
		if (searchSet == null || !searchSet.Any())
		{
			return null;
		}

		var maxDistanceSquared = maxDistance * maxDistance;

		while (FindClosestThing(searchSet, center, out var i) is { } closestThing)
		{
			searchSet.RemoveAt(i);
			if (!closestThing.Spawned)
			{
				continue;
			}

			if ((center - closestThing.Position).LengthHorizontalSquared > maxDistanceSquared)
			{
				break;
			}

			if (!map.reachability.CanReach(center, closestThing, peMode, traverseParams))
			{
				continue;
			}

			if (validator == null || validator(closestThing))
			{
				return closestThing;
			}
		}

		return null;
	}

	public static Thing FindClosestThing(List<Thing> searchSet, IntVec3 center, out int index)
	{
		if (!searchSet.Any())
		{
			index = -1;
			return null;
		}

		var closestThing = searchSet[0];
		index = 0;
		var closestThingSquaredLength = (center - closestThing.Position).LengthHorizontalSquared;
		var count = searchSet.Count;
		for (var i = 1; i < count; i++)
		{
			if (closestThingSquaredLength > (center - searchSet[i].Position).LengthHorizontalSquared)
			{
				closestThing = searchSet[i];
				index = i;
				closestThingSquaredLength = (center - closestThing.Position).LengthHorizontalSquared;
			}
		}
		return closestThing;
	}

	public class CellAllocation
	{
		public Thing allocated;
		public int capacity;

		public CellAllocation(Thing a, int c)
		{
			allocated = a;
			capacity = c;
		}
	}

	public static int CapacityAt(Thing thing, IntVec3 storeCell, Map map)
	{
		if (HoldMultipleThings_Support.CapacityAt(thing, storeCell, map, out var capacity))
		{
			Log.Message($"Found external capacity of {capacity}");
			return capacity;
		}

		return storeCell.GetItemStackSpaceLeftFor(map, thing.def);
	}

	public static bool Stackable(Thing nextThing, KeyValuePair<StoreTarget, CellAllocation> allocation)
		=> nextThing == allocation.Value.allocated
		|| allocation.Value.allocated.CanStackWith(nextThing)
		|| HoldMultipleThings_Support.StackableAt(nextThing, allocation.Key.cell, nextThing.Map);

	public static bool AllocateThingAtCell(Dictionary<StoreTarget, CellAllocation> storeCellCapacity, Pawn pawn, Thing nextThing, Job job)
	{
		var map = pawn.Map;
		var allocation = storeCellCapacity.FirstOrDefault(kvp =>
			kvp.Key is var storeTarget
			&& (storeTarget.container?.TryGetInnerInteractableThingOwner().CanAcceptAnyOf(nextThing)
			?? storeTarget.cell.GetSlotGroup(map).parent.Accepts(nextThing))
			&& Stackable(nextThing, kvp));
		var storeCell = allocation.Key;

		//Can't stack with allocated cells, find a new cell:
		if (storeCell == default)
		{
			var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);
			if (TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, out var nextStoreCell, out var haulDestination, out var innerInteractableThingOwner))
			{
				if (innerInteractableThingOwner is null)
				{
					storeCell = new(nextStoreCell);
					job.targetQueueB.Add(nextStoreCell);

					storeCellCapacity[storeCell] = new(nextThing, CapacityAt(nextThing, nextStoreCell, map));

					Log.Message($"New cell for unstackable {nextThing} = {nextStoreCell}");
				}
				else
				{
					var destinationAsThing = (Thing)haulDestination;
					storeCell = new(destinationAsThing);
					job.targetQueueB.Add(destinationAsThing);

					storeCellCapacity[storeCell] = new(nextThing, innerInteractableThingOwner.GetCountCanAccept(nextThing));

					Log.Message($"New haulDestination for unstackable {nextThing} = {haulDestination}");
				}
			}
			else
			{
				Log.Message($"{nextThing} can't stack with allocated cells");

				if (job.targetQueueA.NullOrEmpty())
				{
					job.targetQueueA.Add(nextThing);
					job.countQueue.Add(nextThing.stackCount);
				}

				return false;
			}
		}

		job.targetQueueA.Add(nextThing);
		var count = nextThing.stackCount;
		storeCellCapacity[storeCell].capacity -= count;
		Log.Message($"{pawn} allocating {nextThing}:{count}, now {storeCell}:{storeCellCapacity[storeCell].capacity}");

		while (storeCellCapacity[storeCell].capacity <= 0)
		{
			var capacityOver = -storeCellCapacity[storeCell].capacity;
			storeCellCapacity.Remove(storeCell);

			Log.Message($"{pawn} overdone {storeCell} by {capacityOver}");

			if (capacityOver == 0)
			{
				break;  //don't find new cell, might not have more of this thing to haul
			}

			var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);
			if (TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, out var nextStoreCell, out var nextHaulDestination, out var innerInteractableThingOwner))
			{
				if (innerInteractableThingOwner is null)
				{
					storeCell = new(nextStoreCell);
					job.targetQueueB.Add(nextStoreCell);

					var capacity = CapacityAt(nextThing, nextStoreCell, map) - capacityOver;
					storeCellCapacity[storeCell] = new(nextThing, capacity);

					Log.Message($"New cell {nextStoreCell}:{capacity}, allocated extra {capacityOver}");
				}
				else
				{
					var destinationAsThing = (Thing)nextHaulDestination;
					storeCell = new(destinationAsThing);
					job.targetQueueB.Add(destinationAsThing);

					var capacity = innerInteractableThingOwner.GetCountCanAccept(nextThing) - capacityOver;

					storeCellCapacity[storeCell] = new(nextThing, capacity);

					Log.Message($"New haulDestination {nextHaulDestination}:{capacity}, allocated extra {capacityOver}");
				}
			}
			else
			{
				count -= capacityOver;
				job.countQueue.Add(count);
				Log.Message($"Nowhere else to store, allocated {nextThing}:{count}");
				return false;
			}
		}
		job.countQueue.Add(count);
		Log.Message($"{nextThing}:{count} allocated");
		return true;
	}

	//public static HashSet<StoreTarget> skipTargets;
	public static HashSet<IntVec3> skipCells;
	public static HashSet<Thing> skipThings;

	public static bool TryFindBestBetterStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, out IHaulDestination haulDestination, out ThingOwner innerInteractableThingOwner)
	{
		var storagePriority = StoragePriority.Unstored;
		innerInteractableThingOwner = null;
		if (TryFindBestBetterStoreCellFor(t, carrier, map, currentPriority, faction, out var foundCell2))
		{
			storagePriority = foundCell2.GetSlotGroup(map).Settings.Priority;
		}

		if (!TryFindBestBetterNonSlotGroupStorageFor(t, carrier, map, currentPriority, faction, out var haulDestination2))
		{
			haulDestination2 = null;
		}

		if (storagePriority == StoragePriority.Unstored && haulDestination2 == null)
		{
			foundCell = IntVec3.Invalid;
			haulDestination = null;
			return false;
		}

		if (haulDestination2 != null && (storagePriority == StoragePriority.Unstored || (int)haulDestination2.GetStoreSettings().Priority > (int)storagePriority))
		{
			foundCell = IntVec3.Invalid;
			haulDestination = haulDestination2;

			if (haulDestination2 is not Thing destinationAsThing)
			{
				Verse.Log.Error($"{haulDestination2} is not a valid Thing. Pick Up And Haul can't work with this");
			}
			else
			{
				innerInteractableThingOwner = destinationAsThing.TryGetInnerInteractableThingOwner();
			}

			if (innerInteractableThingOwner is null)
			{
				Verse.Log.Error($"{haulDestination2} gave null ThingOwner during lookup in Pick Up And Haul's WorkGiver_HaulToInventory");
			}

			return true;
		}

		foundCell = foundCell2;
		haulDestination = foundCell2.GetSlotGroup(map).parent;
		return true;
	}

	public static bool TryFindBestBetterStoreCellFor(Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell)
	{
		var haulDestinations = map.haulDestinationManager.AllGroupsListInPriorityOrder;
		var origin = thing.SpawnedOrAnyParentSpawned ? thing.PositionHeld : carrier.PositionHeld;
		var foundPriority = currentPriority;
		var closestDistSquared = int.MaxValue;
		foundCell = IntVec3.Invalid;
		for (var i = 0; i < haulDestinations.Count; i++)
		{
			var slotGroup = haulDestinations[i];
			var priority = slotGroup.Settings.Priority;
			if (priority < foundPriority || priority <= currentPriority)
			{
				break;
			}

			if ((slotGroup.parent is Thing parentThing && parentThing.Faction != faction)
				|| !slotGroup.parent.HaulDestinationEnabled
				|| !slotGroup.parent.Accepts(thing))
			{
				continue;
			}

			var cellsList = slotGroup.CellsList;
			var sampleSize = cellsList.Count / 64;
			for (var j = 0; j < cellsList.Count; j++)
			{
				var cell = cellsList[j];
				if (cell == default || (skipCells != null && skipCells.Contains(cell)))
				{
					continue;
				}

				var distSquared = (origin - cell).LengthHorizontalSquared;
				if (distSquared > closestDistSquared || !StoreUtility.IsGoodStoreCell(cell, map, thing, carrier, faction))
				{
					continue;
				}

				foundCell = cell;
				closestDistSquared = distSquared;
				foundPriority = priority;
				if (j >= sampleSize)
				{
					break;
				}
			}
		}

		if (!foundCell.IsValid)
		{
			return false;
		}

		skipCells?.Add(foundCell);
		return true;
	}

	public static bool TryFindBestBetterNonSlotGroupStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IHaulDestination haulDestination, bool acceptSamePriority = false)
	{
		var allHaulDestinationsListInPriorityOrder = map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder;
		var intVec = t.SpawnedOrAnyParentSpawned ? t.PositionHeld : carrier.PositionHeld;
		var num = float.MaxValue;
		var storagePriority = StoragePriority.Unstored;
		haulDestination = null;
		for (var i = 0; i < allHaulDestinationsListInPriorityOrder.Count; i++)
		{
			var iHaulDestination = allHaulDestinationsListInPriorityOrder[i];

			if (iHaulDestination is ISlotGroupParent || (iHaulDestination is Building_Grave && !t.CanBeBuried()) || !iHaulDestination.HaulDestinationEnabled)
			{
				continue;
			}

			var priority = iHaulDestination.GetStoreSettings().Priority;
			if ((int)priority < (int)storagePriority || (acceptSamePriority && (int)priority < (int)currentPriority) || (!acceptSamePriority && (int)priority <= (int)currentPriority))
			{
				break;
			}

			float num2 = intVec.DistanceToSquared(iHaulDestination.Position);
			if (num2 > num || !iHaulDestination.Accepts(t))
			{
				continue;
			}

			if (iHaulDestination is Thing thing)
			{
				if ((skipThings != null && skipThings.Contains(thing)) || thing.Faction != faction)
				{
					continue;
				}

				if (thing is IHaulEnroute enroute)
				{
					if (!map.reservationManager.OnlyReservationsForJobDef(thing, JobDefOf.HaulToContainer)
						|| enroute.GetSpaceRemainingWithEnroute(t.def) <= 0
						|| (carrier != null && (thing.IsForbidden(carrier)
							|| !carrier.Map.reachability.CanReach(intVec, thing, PathEndMode.ClosestTouch, TraverseParms.For(carrier)))))
					{
						continue;
					}
				}
				else if (carrier != null)
				{
					if (thing.IsForbidden(carrier)
						|| !carrier.CanReserveNew(thing)
						|| !carrier.Map.reachability.CanReach(intVec, thing, PathEndMode.ClosestTouch, TraverseParms.For(carrier)))
					{
						continue;
					}
				}
				else if (faction != null)
				{
					if (thing.IsForbidden(faction) || map.reservationManager.IsReservedByAnyoneOf(thing, faction))
					{
						continue;
					}
				}
			}
			else
			{
				//not supported. Seems dumb
				continue;

				//if (carrier != null && !carrier.Map.reachability.CanReach(intVec, iHaulDestination.Position, PathEndMode.ClosestTouch, TraverseParms.For(carrier)))
				//	continue;
			}

			num = num2;
			storagePriority = priority;
			haulDestination = iHaulDestination;
		}

		if (haulDestination is Thing chosen)
		{
			skipThings?.Add(chosen);
		}
		return haulDestination != null;
	}
}

public static class PickUpAndHaulDesignationDefOf
{
	public static DesignationDef haulUrgently = DefDatabase<DesignationDef>.GetNamedSilentFail("HaulUrgentlyDesignation");
}
