using System.Reflection;
using HarmonyLib;

namespace PickUpAndHaul;

static class CartCompat
{
	static readonly Func<Thing, object> cargoOwner;

	static CartCompat()
	{
		if (!ModCompatibilityCheck.CartModIsActive)
		{
			return;
		}

		var cargoAccess = AccessTools.TypeByName("WoodenCart.CartCargoAccess");
		var owner = cargoAccess == null ? null : AccessTools.Method(cargoAccess, "Owner", new[] { typeof(Thing) });
		if (owner == null || owner.ReturnType.IsValueType)
		{
			return;
		}

		try
		{
			cargoOwner = (Func<Thing, object>)Delegate.CreateDelegate(typeof(Func<Thing, object>), owner);
		}
		catch (Exception)
		{
			cargoOwner = thing => owner.Invoke(null, new object[] { thing });
		}
	}

	public static bool IsCartCargo(Thing thing)
	{
		if (cargoOwner == null || thing is not { Spawned: true })
		{
			return false;
		}

		try
		{
			return cargoOwner(thing) != null;
		}
		catch (Exception)
		{
			return false;
		}
	}
}
