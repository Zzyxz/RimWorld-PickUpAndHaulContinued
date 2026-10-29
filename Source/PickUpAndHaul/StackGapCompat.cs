using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace PickUpAndHaul;

static class StackGapCompat
{
	const string StackGapPatchType = "StorageUpperBound.PickUpAndHaul_Patch";
	const string StorageManagerType = "StorageUpperBound.StorageManager";
	const string StackGapDataType = "StorageUpperBound.StackGapData";
	const string BabyCarryPatchType = "b4ttl3m3ds.simplebabycarry.ModCompat.PickUpAndHaulCompat";

	static Func<IntVec3, Map, object> findRule;
	static MethodInfo affectUp;
	static MethodInfo affectDn;

	public static void Apply(Harmony harmony)
	{
		if (ModCompatibilityCheck.StackGapIsActive)
		{
			ApplyStackGap(harmony);
		}

		if (ModCompatibilityCheck.SimpleBabyCarryIsActive)
		{
			ApplyBabyCarry(harmony);
		}
	}

	static void ApplyStackGap(Harmony harmony)
	{
		var patchType = AccessTools.TypeByName(StackGapPatchType);
		var managerType = AccessTools.TypeByName(StorageManagerType);
		var dataType = AccessTools.TypeByName(StackGapDataType);
		var find = managerType == null ? null : AccessTools.Method(managerType, "FindOrNull", new[] { typeof(IntVec3), typeof(Map) });
		affectUp = dataType == null ? null : AccessTools.Method(dataType, "AffectUp", Type.EmptyTypes);
		affectDn = dataType == null ? null : AccessTools.Method(dataType, "AffectDn", Type.EmptyTypes);
		if (patchType == null || find == null || affectUp == null || affectDn == null)
		{
			Verse.Log.Warning("Pick Up and Haul - Continued: Stack Gap support could not be applied.");
			return;
		}

		try
		{
			findRule = (Func<IntVec3, Map, object>)Delegate.CreateDelegate(typeof(Func<IntVec3, Map, object>), find);
		}
		catch (Exception e)
		{
			Verse.Log.Warning($"Pick Up and Haul - Continued: Stack Gap support could not be applied. {e.Message}");
			return;
		}

		Hook(harmony, AccessTools.Method(typeof(WorkGiver_HaulToInventory), nameof(WorkGiver_HaulToInventory.CapacityAt)),
			AccessTools.Method(patchType, "CapacityAtPostfix"), "Stack Gap");
	}

	static void ApplyBabyCarry(Harmony harmony)
	{
		var patchType = AccessTools.TypeByName(BabyCarryPatchType);
		Hook(harmony, AccessTools.Method(typeof(JobDriver_UnloadYourHauledInventory), "PullItemFromInventory"),
			patchType == null ? null : AccessTools.Method(patchType, "Postfix_PullItemFromInventory"), "Simple Baby Carry");
	}

	public static bool HasRuleAt(IntVec3 cell, Map map)
	{
		if (findRule == null)
		{
			return false;
		}

		try
		{
			var rule = findRule(cell, map);
			return rule != null && ((bool)affectUp.Invoke(rule, null) || (bool)affectDn.Invoke(rule, null));
		}
		catch (Exception)
		{
			return false;
		}
	}

	static void Hook(Harmony harmony, MethodInfo target, MethodInfo postfix, string modName)
	{
		if (target == null || postfix == null)
		{
			Verse.Log.Warning($"Pick Up and Haul - Continued: {modName} support could not be applied.");
			return;
		}

		var info = Harmony.GetPatchInfo(target);
		if (info != null && info.Postfixes.Any(p => SameMethod(p.PatchMethod, postfix)))
			return;

		try
		{
			harmony.Patch(target, postfix: new HarmonyMethod(postfix));
		}
		catch (Exception e)
		{
			Verse.Log.Warning($"Pick Up and Haul - Continued: {modName} support could not be applied. {e.Message}");
		}
	}

	static bool SameMethod(MethodInfo a, MethodInfo b)
		=> a != null && a.Name == b.Name && a.DeclaringType?.FullName == b.DeclaringType?.FullName;
}
