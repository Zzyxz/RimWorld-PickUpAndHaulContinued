namespace PickUpAndHaul;

public class CompHauledToInventory : ThingComp
{
	private HashSet<Thing> takenToInventory = new();

	public HashSet<Thing> GetHashSet()
	{
		takenToInventory.RemoveWhere(x => x == null);
		return takenToInventory;
	}

	public bool HasAny => takenToInventory is { Count: > 0 };

	public bool Contains(Thing thing) => takenToInventory != null && takenToInventory.Contains(thing);

	public void RegisterHauledItem(Thing thing) => takenToInventory.Add(thing);

	public override void PostExposeData()
	{
		base.PostExposeData();
		Scribe_Collections.Look(ref takenToInventory, "ThingsHauledToInventory", LookMode.Reference);
	}
}