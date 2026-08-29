#if MELONLOADER
using MelonLoader;
using OperatorKillHouse;

[assembly: MelonInfo(
    typeof(OperatorKillHousePlugin),
    OperatorKillHousePlugin.PluginName,
    OperatorKillHousePlugin.PluginVersion,
    "OPERATOR Modding Project")]
[assembly: MelonProcess("OPERATOR")]
[assembly: MelonPlatform(MelonPlatformAttribute.CompatiblePlatforms.WINDOWS_X64)]
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.IL2CPP)]
[assembly: HarmonyDontPatchAll]
[assembly: MelonAdditionalDependencies(
    "OperatorModAPI.MelonLoader",
    "OperatorModdedOperations.MelonLoader")]
#endif
