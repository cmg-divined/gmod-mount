# Garry's Mod Mount for s&box

Load Garry's Mod content directly in s&box. Models, materials, textures.

## What it does

This mount reads GMod's VPK archives and Workshop addons (GMA files) and makes the content available in s&box. You can spawn props, NPCs, playermodels, and whatever else you've got installed - including your subscribed Workshop content.

## Supported formats

**Content Sources**
- VPK (Valve PacK) - versions 1 and 2 (base game content)
- GMA (Garry's Mod Addon) - Workshop subscriptions
- Loose files - manually installed content in garrysmod/models, materials, sound

**Models (MDL)**
- Versions 44, 45, 49, and 53
- Full skeleton/bone support
- Body groups (toggle model variants)
- Skin families (material groups are registered, but have rendering issues - see Notes)
- Vertex weights
- Ragdoll physics from .phy files (joints, constraints, collision hulls)

**Textures (VTF)**
- Most common formats (DXT1, DXT3, DXT5, BGR888, BGRA8888, etc.)
- Mipmap support

**Materials (VMT)**
- Converts Source shaders to s&box's complex shader
- Supports custom handling for different pseudopbr techniques, including MWB materials, BFT's method, EXOPBR, MadIvan materials etc (based on Xenthio's work from his RTX Remix project: https://github.com/Xenthio/garrys-mod-rtx-remixed)
- Base textures and normal maps
- Transparency ($translucent, $alphatest)
- Self-illumination

## Installation

1. Drop the `gmod_mount` folder into your s&box project
2. Make sure you have Garry's Mod installed
3. The mount auto-detects your GMod installation via Steam

## Usage

Once installed, GMod content shows up in the asset browser under the `garrysmod` mount. Browse and spawn stuff.

Content is organized by source:
```
Garry's Mod/
├── models/          # Base game content (VPKs)
├── materials/       
├── sound/
├── addons/          # Workshop addons (GMA files)
│   ├── cool_weapons/
│   │   ├── models/
│   │   └── materials/
│   └── playermodels/
└── custom/          # Manually installed loose files
    ├── models/
    └── materials/
```

Example paths:
```
mount://garrysmod/models/props_c17/oildrum001.vmdl
mount://garrysmod/addons/my_addon/models/weapons/cool_gun.vmdl
mount://garrysmod/custom/models/my_custom_model.vmdl
```

## Notes

- Materials are converted to use s&box's PBR shader with metalness=0 and roughness=1. Source engine materials weren't PBR, so this is a reasonable default.
- Physics hulls are simplified using QEM to keep collision meshes reasonable.
- **Skin families**: Material groups are correctly set up, but s&box's `SetMaterialGroup` doesn't work correctly for procedural models (the engine doesn't track mesh-material-index relationships the same way compiled VMDL models do). The code is in place for when/if this gets fixed.
- Some older or weird models might not work perfectly. If you find something broken, open an issue.

## Structure

```
gmod_mount/
├── Editor/
│   ├── GModMount.cs              # Main mount class, archive loading
│   ├── Resource/
│   │   ├── GModModel.cs          # Model loader (VPK)
│   │   ├── GModMaterial.cs       # Material loader (VPK)
│   │   ├── GModResourceGma.cs    # Resource loaders for GMA addons
│   │   └── GModResourceLoose.cs  # Resource loaders for loose files
│   └── Source/
│       ├── MdlFile.cs            # MDL parser
│       ├── VvdFile.cs            # VVD (vertex data) parser
│       ├── VtxFile.cs            # VTX (triangle strips) parser
│       ├── VtfFile.cs            # VTF (texture) parser
│       ├── VmtFile.cs            # VMT (material) parser
│       ├── PhyFile.cs            # PHY (physics) parser
│       ├── VpkArchive.cs         # VPK archive reader
│       ├── GmaArchive.cs         # GMA addon reader
│       └── SourceModelLoader.cs  # Converts parsed data to s&box Model
```

## Credits

Built with reference to Crowbar's source model parsing code and existing s&box mounts.
