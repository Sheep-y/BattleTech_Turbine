# Turbine 2018.08.01 #
For BATTLETECH 1.1.2

Turbine is a BattleTech mod that lighten and speed up the game's resource loading.
If your game has a tendency to crash when starting a new game, loading a save game, or mech bay, this mod *may* fix your problem.

Because of its low-level nature and the scale of the problem, it is rather delicate.

If it works for you, you should see an obvious improvement in your game's loading time.
If it does not work, your game will crash or hang.  Delete the mod to revert it to normal.

For this reason, this mod cannot be downloaded from Nexus.
If you want to talk about it, open issues on GitHub or join the [modding Discord](https://discord.gg/cnyQUch)

GitHub: https://github.com/Sheep-y/BattleTech_Turbine

This mod does not modify game data or save games.


# Installation

1. [Install BTML](https://github.com/Mpstark/ModTek/wiki/The-Drop-Dead-Simple-Guide-to-Installing-BTML-&-ModTek-&-ModTek-mods). ModTek is not necessary.
2. [Download this mod](https://github.com/Sheep-y/BattleTech_Turbine/releases), extract in the mod folder. i.e. You should see `BATTLETECH\Mods\Turbine.dll`.
3. Launch and play as normal.  This mod has no settings.
4. If the game crash or hang up during a loading screen or blank screen, delete the mod and try again.


# Compatibility

Turbine is NOT expected to work with any mod that also speed up resource loading, such as WhySoSlow.

It should otherwise work with all mods, including those that add new files for the game to load.


# How It Works

The mod has a few functional parts.

First is the request filter.  A cheap and safe check is done after every `DataManagerRequestCompleteMessage` creation.
If the request is invalid or same as last one, it is filtered out.

Then we have the compressor.
It is a pretty big rewrite of `BattleTech.Data.DataManager`, totally replaces two request queues and takes over their management.
A Dictionary is used to speed up matching of incoming request against queued requests, improving engine efficiently.

Bypass come after the compressor.  It works on MechDefs, big request blocks that cost lot of fuel to burn.
When a new MechDef is encountered, it is allowed through and the compressor is signalled to trace it.
Once it is through, we have captured its request dependency list, which opens the bypass tunnel for the MechDef.

If anything cause the MechDef to re-enter the engine, it will now bypass its original dependency processing.
This continues until all its dependencies are processed.  Then the MechDef is manually summoned back.
Its bypass will stay open; only one MechDef may pass through the engine at any one time.  The others are still bypassed.

Thanks to the bypass, the engine can skip lots of unnecessary work and can fit into a smaller call stack.

Requests that are not bypassed will go through a turbine, that the original engine does not have.
It is a loading queue, maintained separately from the full load queue, that drives the compressor's state checks.

As you can expect, all these parts work together to make the game's resource engine more efficient.
If the turbine broke, the compressor may cease to spin, and the whole engine stops.
If the bypass took too much requests away, the turbine may stops and again the whole engine stops.
If an explosion happened that damaged the compressor, you can expect the whole engine to breakdown.

As a safety measure, the mod has a kill switch, that is triggered when it detects any explosion (not same as engine stop).
When the unfortunate happens, the whole mod will disintegrates and falls away, leaving bare the original engine.
This is best happened during part installation.  If any part does not fit, perhaps because of game update, all parts will break away.
Unlike Hollywood movie, though, engine repair rarely works mid-flight.  In the whole bypass development, it saved a running game once.

Finally, the mod has a black box.  On every launch, it keeps a log of every single parts that are installed.
But its real value is its sensors, installed in all parts, that allow it to log explosions.  The result is saved in Log_Turbine.txt.


# Credits

* Thanks [Denedan](https://github.com/Denadan) for finding the two original [performance](https://github.com/saltyhotdog/BattletechIssueTracker/issues/14) [issues](https://github.com/saltyhotdog/BattletechIssueTracker/issues/17)
* Thanks LadyAlekto and many brave RogueTech users on the BATTLETECHGAME discord for testing the mod despite its high tendency to explode the game.
* Thanks HBS the game developer for giving me a ComStar experience when working on this mod.  Can't get any closer to repairing Lostech.