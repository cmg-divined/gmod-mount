# Garry's Mod Mount for s&box

Load Garry's Mod content directly in s&box. Models, materials, textures.

## What it does

This mount reads GMod's VPK archives and makes the content available in s&box. You can spawn props, NPCs, playermodels, and whatever else you've got installed.

## Supported formats

**Models (MDL)**
- Versions 44, 45, 49, and 53
- Full skeleton/bone support
- Body groups
- Skin families
- Vertex weights
- Ragdoll physics from .phy files (joints, constraints, collision hulls)

**Textures (VTF)**
- Most common formats (DXT1, DXT3, DXT5, BGR888, BGRA8888, etc.)
- Mipmap support

**Materials (VMT)**
- Converts Source shaders to s&box's complex shader
- Base textures and normal maps
- Transparency ($translucent, $alphatest)
- Self-illumination

## Installation

1. Drop the `gmod_mount` folder into your s&box project
2. Make sure you have Garry's Mod installed
3. The mount auto-detects your GMod installation via Steam

## Usage

Once installed, GMod content shows up in the asset browser under the `garrysmod` mount. Browse and spawn stuff like you would with any other content.

Models are available at paths like:
```
mount://garrysmod/models/props_c17/oildrum001.vmdl
mount://garrysmod/models/humans/group01/male_07.vmdl
```

## Notes

- Materials are converted to use s&box's PBR shader with metalness=0 and roughness=1 (matte finish). Source engine materials weren't PBR, so this is a reasonable default.
- Physics hulls are simplified using QEM to keep collision meshes reasonable.
- Some older or weird models might not work perfectly. If you find something broken, open an issue.

## Structure

```
gmod_mount/
├── Editor/
│   ├── GModMount.cs          # Main mount class, VPK loading
│   ├── Resource/
│   │   ├── GModModel.cs      # Model resource loader
│   │   └── GModMaterial.cs   # Material resource loader
│   └── Source/
│       ├── MdlFile.cs        # MDL parser
│       ├── MdlStructs.cs     # MDL data structures
│       ├── VvdFile.cs        # VVD (vertex data) parser
│       ├── VtxFile.cs        # VTX (triangle strips) parser
│       ├── VtfFile.cs        # VTF (texture) parser
│       ├── VmtFile.cs        # VMT (material) parser
│       ├── PhyFile.cs        # PHY (physics) parser
│       ├── PhyStructs.cs     # Physics data structures
│       ├── VpkArchive.cs     # VPK archive reader
│       └── SourceModelLoader.cs  # Converts parsed data to s&box Model
```

## Credits

Built with reference to Crowbar's source model parsing code and existing s&box mounts.
