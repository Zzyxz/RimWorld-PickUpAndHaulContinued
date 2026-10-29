using System.Linq;

namespace PickUpAndHaul;
public static class ModCompatibilityCheck
{
	public static bool CombatExtendedIsActive { get; } = IsActive("CETeam.CombatExtended");

	public static bool AllowToolIsActive { get; } = IsActive("UnlimitedHugs.AllowTool");

	public static bool AchtungIsActive { get; } = IsActive("brrainz.achtung");

	public static bool SimpleBabyCarryIsActive { get; } = IsActive("b4ttl3m3ds.simplebabycarry");

	public static bool StackGapIsActive { get; } = IsActive("andromeda.stackgap");

	public static bool ExtendedStorageIsActive
	{
		get;
	} = ModsConfig.ActiveModsInLoadOrder.Any(m => m.Name == "ExtendedStorageFluffyHarmonised")
		|| ModsConfig.ActiveModsInLoadOrder.Any(m => m.Name == "Extended Storage")
		|| ModsConfig.ActiveModsInLoadOrder.Any(m => m.Name == "Core SK");

	internal static bool IsActive(string packageId) => ModLister.GetActiveModWithIdentifier(packageId, ignorePostfix: true) != null;
}
