# Mirror

![AI Assisted](https://raw.githubusercontent.com/Gruffuss/badges/main/ai-assisted.png)

A Stationeers mod (BepInEx 5 + StationeersLaunchPad) that lets you place almost any structure mirrored, with no new kits and no new prefabs. A mirrored structure is the same item as the normal one, so IC10 batch instructions (`lb`, `sb`, `sbn`) still find it by its usual prefab hash.

**Back up your save before using it.** The mod stores a marker on every structure you mirror. A save still loads without the mod; mirrored structures then come back unmirrored.

## Use

- Hold a kit and press **M**. While mirroring is on, the placement panel says *Mirrored. Press M to undo.*
- Place as usual; rotation works as for the normal structure.
- M applies to one build: it switches off after you place, when you put the kit away, or when you take another kit.
- Pipes, cables, consoles, logic displays, walls, window shutters, seats, launch mounts and rocket hulls cannot be mirrored.

## Settings

In the StationeersLaunchPad mod settings:

| Setting | Default | Effect |
|---|---|---|
| Controls / ToggleMirror | M | The mirror key. |
| Controls / OneBuildPerPress | on | Off keeps M on until you press it again. |
| Controls / KeepControlsVanilla | on | Off mirrors switches, buttons, knobs, dials and wheels with the structure (only changes how your own game shows them). |
| Diagnostics / Debug | off | Detailed logging for bug reports. It also samples mirrored structures every frame, so leave it off in normal play. |

## Multiplayer

Not tested yet. Every player needs the mod in the same version; LaunchPad refuses a join when it is missing or differs.

## Known limits

- Lettering painted onto a model (for example "FILL TANK" on the gas tank storage) reads backwards on a mirrored copy.
- Unity logs "BoxCollider does not support negative scale or size" once per box collider of a mirrored structure. The collision boxes themselves are correct; removing the warning properly needs the mirror redesigned without negative scale.
- Cloning a rocket loses the mirror on the devices inside it.

## Build

1. Copy `Stationeers.VS.User.props.example` to `Stationeers.VS.User.props` and set your game path.
2. `dotnet build -c Release`. The build copies the DLL and the About folder into your Stationeers mods folder.

Optional offline checks (patch targets, save and network formats, mirror geometry), using .NET 10 and the game's own assemblies:

```
dotnet run --project tools/Verify/Verify.csproj -c Release -- bin/Release/StationeersMirror.dll "<game>/rocketstation_Data/Managed/Assembly-CSharp.dll"
```
