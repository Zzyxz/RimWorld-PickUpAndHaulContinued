using System.Reflection;
using HarmonyLib;

namespace PickUpAndHaul;

static class AchtungCompat
{
	static readonly MethodInfo instanceGetter;
	static readonly MethodInfo hasForcedJob;
	static object cachedInstance;
	static Func<Pawn, bool, bool> cachedHasForcedJob;

	static AchtungCompat()
	{
		if (!ModCompatibilityCheck.AchtungIsActive)
		{
			return;
		}

		var forcedWork = AccessTools.TypeByName("AchtungMod.ForcedWork");
		instanceGetter = forcedWork == null ? null : AccessTools.PropertyGetter(forcedWork, "Instance");
		hasForcedJob = forcedWork == null ? null : AccessTools.Method(forcedWork, "HasForcedJob", new[] { typeof(Pawn), typeof(bool) });
	}

	public static bool UsesAchtungCheck(Pawn pawn, bool forced)
	{
		if (!ModCompatibilityCheck.AchtungIsActive || !pawn.RaceProps.Humanlike)
		{
			return false;
		}

		if (forced || instanceGetter == null || hasForcedJob == null)
		{
			return true;
		}

		object instance;
		try
		{
			instance = instanceGetter.Invoke(null, null);
		}
		catch (Exception)
		{
			return true;
		}
		if (instance == null)
		{
			return false;
		}

		if (!ReferenceEquals(instance, cachedInstance))
		{
			try
			{
				cachedHasForcedJob = (Func<Pawn, bool, bool>)Delegate.CreateDelegate(typeof(Func<Pawn, bool, bool>), instance, hasForcedJob);
			}
			catch (Exception)
			{
				cachedHasForcedJob = null;
			}
			cachedInstance = instance;
		}
		return cachedHasForcedJob == null || cachedHasForcedJob(pawn, false);
	}
}
