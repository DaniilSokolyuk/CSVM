# Extraction

The engine side of asset extraction: finding the player's Crimson Skies install and reading the
files in it. No type here touches the engine except through `OptionsStore.UserOptions`, so the
whole namespace runs in a plain unit test.

One `## src/...` entry per module, body at most 8 lines.

Traps do not live here; the rule is in `docs/architecture.md`.

## src/Extraction/InstallLocator.cs
Finds the install and every file in it without regard to case. `ResolveDirectory` and
`ResolveFile` walk a relative path one segment at a time and answer the disk's spelling (an exact
match wins where a case-sensitive folder holds two), never renaming. `IsInstall` is the silent rule:
`ZBD` plus `GOSDATA/ASSETS`. `Check` judges a picked folder, and names the install when the pick sits
inside it or holds it up to two levels down. `Candidates` lists valid installs: the remembered path,
then Program Files and each fixed drive on Windows, or Wine and every Proton prefix on Linux,
folded through links. Read `RememberedInstall.cs` next.

## src/Extraction/RememberedInstall.cs
The install folder the last extraction read, stored as `OptionsDef.InstallPath` in
`user://options.json`. `Get` and `Set` take an `OptionsStore` for tests, and default to
`OptionsStore.UserOptions()`, which needs the engine. A save loads first and changes only this
field; the value is fully qualified, and the store drops one that is not.
