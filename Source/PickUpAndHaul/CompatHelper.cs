using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace PickUpAndHaul;

internal class CompatHelper
{
	private static readonly Type ceInventoryType = ModCompatibilityCheck.CombatExtendedIsActive ? AccessTools.TypeByName("CombatExtended.CompInventory") : null;
	private static readonly MethodInfo ceUpdateInventory = ceInventoryType == null ? null : AccessTools.Method(ceInventoryType, "UpdateInventory", Type.EmptyTypes);
	private static readonly MethodInfo ceCurrentWeight = ceInventoryType == null ? null : AccessTools.PropertyGetter(ceInventoryType, "currentWeight");
	private static readonly MethodInfo ceCapacityWeight = ceInventoryType == null ? null : AccessTools.PropertyGetter(ceInventoryType, "capacityWeight");
	private static readonly MethodInfo ceCurrentBulk = ceInventoryType == null ? null : AccessTools.PropertyGetter(ceInventoryType, "currentBulk");
	private static readonly MethodInfo ceCapacityBulk = ceInventoryType == null ? null : AccessTools.PropertyGetter(ceInventoryType, "capacityBulk");
	private static StatDef ceBulkStat;
	private static bool ceBulkStatResolved;

	public static bool IsActive { get; private set; } = ceInventoryType != null && ceUpdateInventory != null
		&& ceCurrentWeight != null && ceCapacityWeight != null && ceCurrentBulk != null && ceCapacityBulk != null;

	public static bool CeUnsupported => ModCompatibilityCheck.CombatExtendedIsActive && !IsActive;

	private static ThingComp CeInventory(Pawn pawn)
	{
		if (!IsActive || pawn?.AllComps == null)
		{
			return null;
		}

		var comps = pawn.AllComps;
		for (var i = 0; i < comps.Count; i++)
		{
			if (ceInventoryType.IsInstanceOfType(comps[i]))
			{
				return comps[i];
			}
		}
		return null;
	}

	private static float Read(MethodInfo getter, ThingComp comp) => (float)getter.Invoke(comp, null);

	private static StatDef BulkStat
	{
		get
		{
			if (!ceBulkStatResolved)
			{
				ceBulkStat = DefDatabase<StatDef>.GetNamedSilentFail("Bulk");
				ceBulkStatResolved = true;
			}
			return ceBulkStat;
		}
	}

	public static bool CeOverweight(Pawn pawn)
	{
		var comp = CeInventory(pawn);
		if (comp == null)
		{
			return false;
		}

		var limit = Settings.MaximumOccupiedCapacityToConsiderHauling;
		var capacityWeight = Read(ceCapacityWeight, comp);
		var capacityBulk = Read(ceCapacityBulk, comp);
		return (capacityWeight > 0f && Read(ceCurrentWeight, comp) / capacityWeight >= limit)
			|| (capacityBulk > 0f && Read(ceCurrentBulk, comp) / capacityBulk >= limit);
	}

	public static bool CeFull(Pawn pawn)
	{
		var comp = CeInventory(pawn);
		if (comp == null)
		{
			return false;
		}

		var factor = Settings.HaulCarryFactor;
		return Read(ceCurrentWeight, comp) >= Read(ceCapacityWeight, comp) * factor
			|| Read(ceCurrentBulk, comp) >= Read(ceCapacityBulk, comp) * factor;
	}

	public static int CanFitInInventory(Pawn pawn, Thing thing)
	{
		var comp = CeInventory(pawn);
		if (comp == null)
		{
			return thing.stackCount;
		}

		var factor = Settings.HaulCarryFactor;
		var freeWeight = Read(ceCapacityWeight, comp) * factor - Read(ceCurrentWeight, comp);
		var freeBulk = Read(ceCapacityBulk, comp) * factor - Read(ceCurrentBulk, comp);
		var weight = thing.GetStatValue(StatDefOf.Mass);
		var bulk = BulkPerItem(thing);
		float count = thing.stackCount;
		if (weight > 0f)
		{
			count = Math.Min(count, freeWeight / weight);
		}
		if (bulk > 0f)
		{
			count = Math.Min(count, freeBulk / bulk);
		}
		return Math.Max(0, Mathf.FloorToInt(count));
	}

	public static float AvailableBulk(Pawn pawn)
	{
		var comp = CeInventory(pawn);
		if (comp == null)
		{
			return float.PositiveInfinity;
		}

		return Read(ceCapacityBulk, comp) * Settings.HaulCarryFactor - Read(ceCurrentBulk, comp);
	}

	public static float BulkPerItem(Thing thing)
	{
		var stat = IsActive ? BulkStat : null;
		return stat == null ? 0f : thing.GetStatValue(stat);
	}

	internal static void UpdateInventory(Pawn pawn)
	{
		var comp = CeInventory(pawn);
		if (comp != null && ceUpdateInventory != null)
		{
			ceUpdateInventory.Invoke(comp, null);
		}
	}

	private static FieldInfo ceIntegerValue;
	private static FieldInfo ceRangedWeapons;
	private static FieldInfo ceMeleeWeapons;
	private static Type ceAmmoUserType;
	private static MethodInfo ceAmmoUseAmmo;
	private static MethodInfo ceAmmoCurrentAmmo;
	private static MethodInfo ceAmmoCurMagCount;

	internal static void ApplyPatches(Harmony harmony)
	{
		if (IsActive && BulkStat == null)
		{
			IsActive = false;
		}

		if (!IsActive)
		{
			if (ModCompatibilityCheck.CombatExtendedIsActive)
			{
				Verse.Log.Warning("Pick Up and Haul - Continued: this Combat Extended version is not supported. Colonists haul the normal way until an update.");
			}
			return;
		}

		var holdTracker = AccessTools.TypeByName("CombatExtended.Utility_HoldTracker");
		ceIntegerValue = AccessTools.Field(AccessTools.TypeByName("CombatExtended.Integer"), "value");
		ceRangedWeapons = AccessTools.Field(ceInventoryType, "rangedWeaponListCached");
		ceMeleeWeapons = AccessTools.Field(ceInventoryType, "meleeWeaponListCached");
		ceAmmoUserType = AccessTools.TypeByName("CombatExtended.CompAmmoUser");
		if (ceAmmoUserType != null)
		{
			ceAmmoUseAmmo = AccessTools.PropertyGetter(ceAmmoUserType, "UseAmmo");
			ceAmmoCurrentAmmo = AccessTools.PropertyGetter(ceAmmoUserType, "CurrentAmmo");
			ceAmmoCurMagCount = AccessTools.PropertyGetter(ceAmmoUserType, "CurMagCount");
			if (ceAmmoUseAmmo == null || ceAmmoCurrentAmmo == null || ceAmmoCurMagCount == null)
			{
				ceAmmoUserType = null;
			}
		}

		TryPatch(harmony, holdTracker == null ? null : AccessTools.Method(holdTracker, "GetStorageByThingDef", new[] { typeof(Pawn) }),
			nameof(StorageByThingDef_Postfix), ceIntegerValue != null);
		TryPatch(harmony, holdTracker == null ? null : AccessTools.Method(holdTracker, "GetExcessThing"),
			nameof(GetExcessThing_Postfix), true);
		TryPatch(harmony, ceUpdateInventory, nameof(UpdateInventory_Postfix), ceRangedWeapons != null && ceMeleeWeapons != null);
	}

	private static void TryPatch(Harmony harmony, MethodInfo target, string postfix, bool ready)
	{
		if (target == null || !ready)
		{
			Verse.Log.Warning($"Pick Up and Haul - Continued: Combat Extended support for {postfix.Replace("_Postfix", "")} could not be applied.");
			return;
		}

		try
		{
			harmony.Patch(target, postfix: new HarmonyMethod(typeof(CompatHelper), postfix));
		}
		catch (Exception e)
		{
			Verse.Log.Warning($"Pick Up and Haul - Continued: Combat Extended support for {postfix.Replace("_Postfix", "")} could not be applied. {e.Message}");
		}
	}

	private static HashSet<Thing> HauledThings(Pawn pawn)
	{
		var hauled = pawn?.GetComp<CompHauledToInventory>()?.GetHashSet();
		return hauled is { Count: > 0 } ? hauled : null;
	}

	private static void StorageByThingDef_Postfix(Pawn pawn, object __result)
	{
		if (HauledThings(pawn) is not { } hauled || __result is not System.Collections.IDictionary listing)
		{
			return;
		}

		foreach (var thing in hauled)
		{
			if (thing.Destroyed || !pawn.inventory.innerContainer.Contains(thing))
			{
				continue;
			}

			var inner = thing.GetInnerIfMinified();
			Subtract(listing, inner.def, inner.stackCount);

			var ammoUser = ceAmmoUserType == null ? null : (inner as ThingWithComps)?.AllComps?.FirstOrDefault(c => ceAmmoUserType.IsInstanceOfType(c));
			if (ammoUser != null && (bool)ceAmmoUseAmmo.Invoke(ammoUser, null) && ceAmmoCurrentAmmo.Invoke(ammoUser, null) is ThingDef ammo)
			{
				Subtract(listing, ammo, (int)ceAmmoCurMagCount.Invoke(ammoUser, null));
			}
		}
	}

	private static void Subtract(System.Collections.IDictionary listing, ThingDef def, int count)
	{
		if (count <= 0 || !listing.Contains(def))
		{
			return;
		}

		var entry = listing[def];
		var value = (int)ceIntegerValue.GetValue(entry) - count;
		if (value <= 0)
		{
			listing.Remove(def);
		}
		else
		{
			ceIntegerValue.SetValue(entry, value);
		}
	}

	private static void GetExcessThing_Postfix(Pawn pawn, ref bool __result, ref Thing dropThing, ref int dropCount)
	{
		if (!__result || dropThing == null || HauledThings(pawn) is not { } hauled || !hauled.Contains(dropThing))
		{
			return;
		}

		var def = dropThing.def;
		var inventory = pawn.inventory.innerContainer;
		for (var i = 0; i < inventory.Count; i++)
		{
			var own = inventory[i];
			if (own.def == def && !hauled.Contains(own))
			{
				dropThing = own;
				dropCount = Math.Min(dropCount, own.stackCount);
				return;
			}
		}

		__result = false;
		dropThing = null;
		dropCount = 0;
	}

	private static void UpdateInventory_Postfix(ThingComp __instance)
	{
		if (HauledThings(__instance.parent as Pawn) is not { } hauled)
		{
			return;
		}

		(ceRangedWeapons.GetValue(__instance) as List<ThingWithComps>)?.RemoveAll(hauled.Contains);
		(ceMeleeWeapons.GetValue(__instance) as List<ThingWithComps>)?.RemoveAll(hauled.Contains);
	}
}
