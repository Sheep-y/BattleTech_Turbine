# Turbine 2.0 #
For BATTLETECH 1.3.0

Turbine is a BattleTech mod that lighten and speed up the game's resource loading.

This mod does not modify game data or save games.
This mod does not fix memory leaks, either.  Rest your eyes.

* GitHub: https://github.com/Sheep-y/BattleTech_Turbine
* Nexus Mods: https://www.nexusmods.com/battletech/mods/288

# Installation

1. Install [BTML and ModTek](https://github.com/janxious/ModTek/wiki/The-Drop-Dead-Simple-Guide-to-Installing-BTML-&-ModTek-&-ModTek-mods).
2. [Download this mod](https://github.com/Sheep-y/BattleTech_Turbine/releases), extract in the mod folder. i.e. You should see `BATTLETECH\Mods\Turbine\Turbine.dll`.
3. Launch and play as normal.  This mod has no settings.
4. If the game crash or hang up during a loading screen or blank screen, delete the mod and try again.

Note: Turbine 1.x couldn't be loaded as ModTek, but it has been updated since 2.0.
Please delete old Turbine and do not use both at the same time.


# Compatibility

Tested with janxious's BTML v0.6.4 and ModTek v0.4.2.

It should otherwise work with all mods, including those that add new files for the game to load.


# How It Works

The mod has a few functional parts.
Version 1.x is a major rewrite of BattleTech's DataManager, but its main ideas has since been implemented by the game.

1. The resource load loop has been rewritten to run faster.
2. Two lightweight duplicate resource filters are added, one check loading requests and the other checks complete notifications.
3. The json pre-processor has been rewritten to not use regular expression.
4. The csv line reader (for version manifest) has been optimised to use quick split if possible.
5. Data hashing code has been replaced with a multi-thread implementation.
6. VFX name list is cached instead of rebuilt every time.
7. Unparsed CombatConstants is cached in memory in compressed form.


# Credits

* Thanks [Denedan](https://github.com/Denadan) for finding the two original [performance](https://github.com/saltyhotdog/BattletechIssueTracker/issues/14) [issues](https://github.com/saltyhotdog/BattletechIssueTracker/issues/17)
* Thanks [Matthew Spencer](https://github.com/m22spencer) for doing very detailed and amazing profiling so that I know where to start hacking, and suggested ways to speed the game up further.
* Thanks LadyAlekto and many brave RogueTech users on the BATTLETECHGAME discord for testing the mod despite its high tendency to explode their games.
* Thanks HBS the game developer for giving me a ComStar experience when working on this mod.  Can't get any closer to maintaining Lostech.