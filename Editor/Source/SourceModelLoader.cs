using Sandbox;

namespace GModMount.Source;

/// <summary>
/// Converts Source Engine models to s&box models.
/// Supports MDL versions 48, 49, and 53.
/// </summary>
public static class SourceModelLoader
{
	/// <summary>
	/// Skinned vertex layout for s&box.
	/// </summary>
	private struct SkinnedVertex
	{
		[VertexLayout.Position]
		public Vector3 Position;

		[VertexLayout.Normal]
		public Vector3 Normal;

		[VertexLayout.Tangent]
		public Vector3 Tangent;

		[VertexLayout.TexCoord]
		public Vector2 TexCoord;

		[VertexLayout.BlendIndices]
		public Color32 BlendIndices;

		[VertexLayout.BlendWeight]
		public Color32 BlendWeights;

		public static readonly VertexAttribute[] Layout = new VertexAttribute[]
		{
			new VertexAttribute( VertexAttributeType.Position, VertexAttributeFormat.Float32 ),
			new VertexAttribute( VertexAttributeType.Normal, VertexAttributeFormat.Float32 ),
			new VertexAttribute( VertexAttributeType.Tangent, VertexAttributeFormat.Float32 ),
			new VertexAttribute( VertexAttributeType.TexCoord, VertexAttributeFormat.Float32, 2 ),
			new VertexAttribute( VertexAttributeType.BlendIndices, VertexAttributeFormat.UInt8, 4 ),
			new VertexAttribute( VertexAttributeType.BlendWeights, VertexAttributeFormat.UInt8, 4 )
		};
	}

	private static readonly Material DefaultMaterial = Material.Load( "materials/dev/primary_white.vmat" );

	/// <summary>
	/// Convert a Source model to an s&box Model.
	/// </summary>
	/// <param name="sourceModel">The parsed Source model</param>
	/// <param name="lod">LOD level to use (0 = highest detail)</param>
	/// <param name="skinFamily">Skin family index for material replacement (deprecated, skins are now registered as material groups)</param>
	/// <param name="path">Resource path for the model</param>
	/// <param name="mount">Mount to load materials from</param>
	/// <returns>s&box Model</returns>
	public static Model Convert( SourceModel sourceModel, int lod = 0, int skinFamily = 0, string path = null, GModMount mount = null )
	{
		var builder = Model.Builder;

		if ( !string.IsNullOrEmpty( path ) )
			builder.WithName( path );

		// Build base material list (skin 0)
		var baseMaterials = BuildMaterialList( sourceModel, 0, mount );

		// Add bones
		AddBones( builder, sourceModel );

		// Add meshes from each body part, tracking unique materials used (by instance)
		var uniqueMaterials = new List<(int mdlIndex, Material material)>();
		AddMeshes( builder, sourceModel, baseMaterials, lod, uniqueMaterials );

		// Add material groups for each skin family
		AddMaterialGroups( builder, sourceModel, mount, baseMaterials, uniqueMaterials );

		// Add attachments
		AddAttachments( builder, sourceModel );

		// Add physics collision and ragdoll constraints
		AddPhysics( builder, sourceModel );

		var model = builder.Create();
		
		// Log model creation summary
		if ( model != null )
		{
			Log.Info( $"SourceModelLoader: Created model with {model.Materials.Length} materials, {model.MaterialGroupCount} material groups" );
		}
		
		return model;
	}

	/// <summary>
	/// Build the material list, applying skin family replacements.
	/// </summary>
	private static List<Material> BuildMaterialList( SourceModel model, int skinFamily, GModMount mount )
	{
		var materials = new List<Material>();

		for ( int i = 0; i < model.Mdl.Materials.Count; i++ )
		{
			int materialIndex = i;

			// Apply skin family replacement
			if ( skinFamily > 0 && skinFamily < model.Mdl.SkinFamilies.Count )
			{
				var skinRefs = model.Mdl.SkinFamilies[skinFamily];
				if ( i < skinRefs.Length )
				{
					materialIndex = skinRefs[i];
				}
			}

			materials.Add( LoadMaterial( model, materialIndex, mount ) );
		}

		return materials;
	}

	/// <summary>
	/// Add material groups (skins) to the model.
	/// </summary>
	private static void AddMaterialGroups( ModelBuilder builder, SourceModel model, GModMount mount, List<Material> baseMaterials, List<(int mdlIndex, Material material)> uniqueMaterials )
	{
		var skinFamilies = model.Mdl.SkinFamilies;
		if ( skinFamilies == null || skinFamilies.Count <= 1 )
			return;

		if ( uniqueMaterials.Count == 0 )
		{
			Log.Warning( "SourceModelLoader: No unique materials found, skipping skin groups" );
			return;
		}

		Log.Info( $"SourceModelLoader: Registering {skinFamilies.Count} skin families" );
		Log.Info( $"  Unique materials used ({uniqueMaterials.Count}): [{string.Join( ", ", uniqueMaterials.Select( m => m.mdlIndex ) )}]" );

		var defaultRefs = skinFamilies[0];

		// Each group will only have the base unique materials count (no padding)
		int totalMaterialCount = uniqueMaterials.Count;
		Log.Info( $"  Materials per group: {totalMaterialCount}" );

		// Create material groups for ALL skins including default (skin 0)
		// Each group must have the same number of materials as Model.Materials will have
		for ( int skinIdx = 0; skinIdx < skinFamilies.Count; skinIdx++ )
		{
			var skinName = skinIdx == 0 ? "default" : $"skin{skinIdx}";
			var materialGroup = builder.AddMaterialGroup( skinName );
			var skinRefs = skinFamilies[skinIdx];
			
			Log.Info( $"  Building material group '{skinName}':" );
			
			// Add materials in the exact order they were used by meshes
			int slotIdx = 0;
			foreach ( var (baseMdlIndex, baseMaterial) in uniqueMaterials )
			{
				// Get the replacement material index for this skin
				int resolvedIndex = (baseMdlIndex < skinRefs.Length) ? skinRefs[baseMdlIndex] : baseMdlIndex;
				int defaultIndex = (baseMdlIndex < defaultRefs.Length) ? defaultRefs[baseMdlIndex] : baseMdlIndex;
				
				Material material;
				if ( resolvedIndex != defaultIndex )
				{
					// This material changed - load the replacement
					material = LoadMaterial( model, resolvedIndex, mount );
					var baseName = baseMdlIndex < model.Mdl.Materials.Count ? model.Mdl.Materials[baseMdlIndex] : "?";
					var resolvedName = resolvedIndex < model.Mdl.Materials.Count ? model.Mdl.Materials[resolvedIndex] : "?";
					Log.Info( $"    [{slotIdx}] {baseName} -> {resolvedName}, mat={material?.ResourcePath ?? "null"}" );
				}
				else
				{
					// Use the EXACT same material instance that the mesh uses
					material = baseMaterial;
				}
				
				materialGroup.AddMaterial( material );
				slotIdx++;
			}
		}
		
		Log.Info( $"  Created {skinFamilies.Count} material groups with {totalMaterialCount} materials each" );
	}

	/// <summary>
	/// Load a material by index from the model.
	/// </summary>
	private static Material LoadMaterial( SourceModel model, int materialIndex, GModMount mount )
	{
		// Get material name
		string materialName = materialIndex < model.Mdl.Materials.Count
			? model.Mdl.Materials[materialIndex]
			: null;

		if ( string.IsNullOrEmpty( materialName ) )
			return DefaultMaterial;

		// Try to load the material from the mount
		if ( mount != null )
		{
			// Search in material directories - check if VMT exists before loading
			foreach ( var matDir in model.Mdl.MaterialPaths )
			{
				var matPath = $"materials/{matDir}{materialName}".Replace( "\\", "/" ).Replace( "//", "/" ).TrimStart( '/' );
				var vmtPath = $"{matPath}.vmt";
				var mountPath = $"mount://garrysmod/{matPath}.vmat";
				
				// Only try to load if the VMT file actually exists
				if ( mount.FileExists( vmtPath ) )
				{
					var material = Material.Load( mountPath );
					if ( material != null && material.IsValid() )
						return material;
				}
			}

			// Try without material path prefix
			var directPath = $"materials/{materialName}".Replace( "\\", "/" ).TrimStart( '/' );
			var directVmtPath = $"{directPath}.vmt";
			var directMountPath = $"mount://garrysmod/{directPath}.vmat";
			
			if ( mount.FileExists( directVmtPath ) )
			{
				var material = Material.Load( directMountPath );
				if ( material != null && material.IsValid() )
					return material;
			}
		}

		return DefaultMaterial;
	}

	/// <summary>
	/// Add bones to the model builder.
	/// </summary>
	private static void AddBones( ModelBuilder builder, SourceModel model )
	{
		if ( model.IsStaticProp || model.Mdl.Bones.Count == 0 )
			return;

		// Build bone transforms
		var boneTransforms = new Transform[model.Mdl.Bones.Count];

		for ( int i = 0; i < model.Mdl.Bones.Count; i++ )
		{
			var bone = model.Mdl.Bones[i];

			// Convert from Source to s&box coordinate system
			Vector3 position = ConvertPosition( bone.Position );
			Rotation rotation = ConvertRotation( bone.Quaternion );

			var localTransform = new Transform( position, rotation, 1f );

			// Convert to world space if has parent
			if ( bone.ParentIndex >= 0 && bone.ParentIndex < i )
			{
				localTransform = boneTransforms[bone.ParentIndex].ToWorld( localTransform );
			}

			boneTransforms[i] = localTransform;
		}

		// Add bones to builder
		for ( int i = 0; i < model.Mdl.Bones.Count; i++ )
		{
			var bone = model.Mdl.Bones[i];
			string parentName = bone.ParentIndex >= 0 && bone.ParentIndex < model.Mdl.Bones.Count 
				? model.Mdl.Bones[bone.ParentIndex].Name 
				: null;

			builder.AddBone( bone.Name, boneTransforms[i].Position, boneTransforms[i].Rotation, parentName );
		}
	}

	/// <summary>
	/// Add meshes from all body parts and models.
	/// </summary>
	private static void AddMeshes( ModelBuilder builder, SourceModel model, List<Material> materials, int lod, List<(int mdlIndex, Material material)> uniqueMaterials )
	{
		var vvd = model.Vvd;
		var vtx = model.Vtx;
		var mdl = model.Mdl;

		// Get vertices for this LOD
		var vertices = vvd.GetVerticesForLod( lod );
		var tangents = vvd.Tangents;
		
		Log.Info( $"AddMeshes: VVD has {vertices.Length} vertices for LOD {lod}, VTX has {vtx.BodyParts.Count} body parts" );

		// Track cumulative vertex count across all models (NOT mdlModel.VertexIndex!)
		// This is how Crowbar calculates bodyPartVertexIndexStart
		int bodyPartVertexIndexStart = 0;

		Log.Info( $"SourceModelLoader: Processing {mdl.BodyParts.Count} body parts" );

		// Process each body part
		for ( int bpIdx = 0; bpIdx < mdl.BodyParts.Count && bpIdx < vtx.BodyParts.Count; bpIdx++ )
		{
			var mdlBodyPart = mdl.BodyParts[bpIdx];
			var vtxBodyPart = vtx.BodyParts[bpIdx];
			var bodyPartName = mdlBodyPart.Name;

			Log.Info( $"  BodyPart '{bodyPartName}': {mdlBodyPart.Models.Count} models" );

			// Track whether this body part has any empty choices
			bool hasEmptyChoice = false;
			int nonEmptyModelCount = 0;

			// Process each model in the body part
			for ( int modelIdx = 0; modelIdx < mdlBodyPart.Models.Count && modelIdx < vtxBodyPart.Models.Count; modelIdx++ )
			{
				var mdlModel = mdlBodyPart.Models[modelIdx];
				var vtxModel = vtxBodyPart.Models[modelIdx];

				// Check if this is an empty/blank model
				bool isEmpty = string.IsNullOrEmpty( mdlModel.Name ) || 
				               mdlModel.Name.StartsWith( "blank", StringComparison.OrdinalIgnoreCase ) ||
				               mdlModel.Meshes.Count == 0;

				if ( isEmpty )
				{
					hasEmptyChoice = true;
					// Empty model - still need to register the body group choice
					// Create a tiny invisible mesh so the choice exists in s&box
					Log.Info( $"    Model {modelIdx}: (off) - registering empty body group choice {modelIdx}" );
					var emptyMesh = CreateEmptyMesh( $"{bodyPartName} {modelIdx}" );
					if ( emptyMesh != null )
					{
						builder.AddMesh( emptyMesh, lod, bodyPartName, modelIdx );
					}
					continue;
				}

				nonEmptyModelCount++;
				
				// Get a clean display name from the model (strip .smd extension)
				string modelDisplayName = GetModelDisplayName( mdlModel.Name, modelIdx );
				
				Log.Info( $"    Model {modelIdx}: '{modelDisplayName}' - {mdlModel.Meshes.Count} meshes -> body group '{bodyPartName}' choice {modelIdx}" );

				// Get the LOD data (use requested LOD, or fall back to 0)
				int lodIdx = Math.Min( lod, vtxModel.Lods.Count - 1 );
				if ( lodIdx < 0 ) continue;

				var vtxLod = vtxModel.Lods[lodIdx];

				// Process each mesh
				for ( int meshIdx = 0; meshIdx < mdlModel.Meshes.Count && meshIdx < vtxLod.Meshes.Count; meshIdx++ )
				{
					var mdlMesh = mdlModel.Meshes[meshIdx];
					var vtxMesh = vtxLod.Meshes[meshIdx];

					// Get material for this mesh and track unique material indices
					int materialIndex = mdlMesh.MaterialIndex;
					var material = materialIndex < materials.Count
						? materials[materialIndex]
						: DefaultMaterial;

					// Track unique materials in order of first use (by instance)
					if ( !uniqueMaterials.Any( m => m.material == material ) )
					{
						uniqueMaterials.Add( (materialIndex, material) );
					}

					// Build mesh from strip groups
					// Pass the cumulative vertex start index for proper VVD indexing
					var mesh = BuildMesh(
						model,
						mdlMesh,
						vtxMesh,
						vertices,
						tangents,
						material,
						bodyPartVertexIndexStart,
						$"{bodyPartName} {modelIdx}"
					);

					if ( mesh != null )
					{
						// Add mesh with body group info
						// groupName = body part name, choiceIndex = model index within body part
						builder.AddMesh( mesh, lod, bodyPartName, modelIdx );
					}
				}

				// Add this model's vertex count to the cumulative total
				bodyPartVertexIndexStart += mdlModel.VertexCount;
			}

			// If this body part has only 1 model and no empty choice, add an "off" option
			// This allows users to toggle single-model body groups on/off
			if ( nonEmptyModelCount == 1 && !hasEmptyChoice )
			{
				int offChoiceIndex = mdlBodyPart.Models.Count; // Add as the next choice
				Log.Info( $"    Adding implicit 'off' choice {offChoiceIndex} for single-model body group '{bodyPartName}'" );
				var emptyMesh = CreateEmptyMesh( $"{bodyPartName} {offChoiceIndex}" );
				if ( emptyMesh != null )
				{
					builder.AddMesh( emptyMesh, lod, bodyPartName, offChoiceIndex );
				}
			}
		}
	}

	/// <summary>
	/// Get a clean display name for a model.
	/// </summary>
	private static string GetModelDisplayName( string modelName, int modelIndex )
	{
		if ( string.IsNullOrEmpty( modelName ) )
			return $"submodel {modelIndex}";

		// Strip .smd extension if present
		var name = modelName;
		if ( name.EndsWith( ".smd", StringComparison.OrdinalIgnoreCase ) )
			name = name.Substring( 0, name.Length - 4 );

		return name;
	}

	/// <summary>
	/// Build a single mesh from VTX strip groups.
	/// </summary>
	private static Mesh BuildMesh(
		SourceModel model,
		MdlMesh mdlMesh,
		VtxMeshData vtxMesh,
		VvdVertex[] vertices,
		VvdTangent[] tangents,
		Material material,
		int bodyPartVertexIndexStart,
		string meshName )
	{
		var meshVertices = new List<SkinnedVertex>();
		var meshIndices = new List<int>();
		var bounds = new BBox();

		int totalSkippedTriangles = 0;
		int totalTriangles = 0;
		
		foreach ( var stripGroup in vtxMesh.StripGroups )
		{
			int baseVertex = meshVertices.Count;
			int stripGroupVertexCount = stripGroup.Vertices.Length;

			// Add vertices from this strip group
			for ( int i = 0; i < stripGroupVertexCount; i++ )
			{
				var vtxVertex = stripGroup.Vertices[i];
				// Correct formula based on Crowbar:
				// vertexIndex = originalMeshVertexIndex + bodyPartVertexIndexStart + meshVertexIndexStart
				int vvdIndex = vtxVertex.OriginalMeshVertexIndex + bodyPartVertexIndexStart + mdlMesh.VertexOffset;

				if ( vvdIndex < 0 || vvdIndex >= vertices.Length )
				{
					// Add a default vertex to maintain index alignment (will create degenerate triangles)
					meshVertices.Add( new SkinnedVertex
					{
						Position = Vector3.Zero,
						Normal = Vector3.Up,
						Tangent = Vector3.Forward,
						TexCoord = Vector2.Zero,
						BlendIndices = new Color32( 0, 0, 0, 0 ),
						BlendWeights = new Color32( 255, 0, 0, 0 )
					} );
					continue;
				}

				var vvdVertex = vertices[vvdIndex];

				// Convert position
				Vector3 position = ConvertPosition( vvdVertex.Position );
				Vector3 normal = ConvertDirection( vvdVertex.Normal );

				// Get tangent if available
				Vector3 tangent = Vector3.Forward;
				if ( tangents != null && vvdIndex < tangents.Length )
				{
					tangent = ConvertDirection( tangents[vvdIndex].AsVector3 );
				}

				// Build blend indices and weights
				Color32 blendIndices = new Color32(
					vvdVertex.Bone0,
					vvdVertex.Bone1,
					vvdVertex.Bone2,
					0
				);

				Color32 blendWeights = NormalizeWeights(
					vvdVertex.Weight0,
					vvdVertex.Weight1,
					vvdVertex.Weight2
				);

				meshVertices.Add( new SkinnedVertex
				{
					Position = position,
					Normal = normal,
					Tangent = tangent,
					TexCoord = vvdVertex.TexCoord,
					BlendIndices = blendIndices,
					BlendWeights = blendWeights
				} );

				bounds = bounds.AddPoint( position );
			}

			// Add indices from each strip
			// Note: We reverse the winding order because the coordinate system conversion
			// includes a reflection (-X component) which inverts triangle facing
			int indicesLength = stripGroup.Indices?.Length ?? 0;
			
			foreach ( var strip in stripGroup.Strips )
			{
				// Validate strip offset is within bounds
				if ( strip.IndexOffset < 0 || strip.IndexOffset >= indicesLength )
				{
					Log.Warning( $"BuildMesh: Strip IndexOffset {strip.IndexOffset} out of bounds (indices length: {indicesLength})" );
					continue;
				}
				
				if ( strip.IsTriList )
				{
					// Triangle list - reverse winding (0,1,2) -> (0,2,1)
					for ( int i = 0; i < strip.IndexCount; i += 3 )
					{
						int idx0 = strip.IndexOffset + i;
						int idx1 = strip.IndexOffset + i + 1;
						int idx2 = strip.IndexOffset + i + 2;
						
						// Validate all indices are in bounds
						if ( idx0 < 0 || idx0 >= indicesLength ||
						     idx1 < 0 || idx1 >= indicesLength ||
						     idx2 < 0 || idx2 >= indicesLength )
							continue;
						
						int v0 = stripGroup.Indices[idx0];
						int v1 = stripGroup.Indices[idx1];
						int v2 = stripGroup.Indices[idx2];
						
						totalTriangles++;
						
						// Validate vertex indices are within the strip group's vertex range
						if ( v0 < 0 || v0 >= stripGroupVertexCount ||
						     v1 < 0 || v1 >= stripGroupVertexCount ||
						     v2 < 0 || v2 >= stripGroupVertexCount )
						{
							totalSkippedTriangles++;
							continue;
						}
						
						meshIndices.Add( baseVertex + v0 );
						meshIndices.Add( baseVertex + v2 );
						meshIndices.Add( baseVertex + v1 );
					}
				}
				else if ( strip.IsTriStrip )
				{
					// Triangle strip - convert to triangle list with reversed winding
					for ( int i = 0; i < strip.IndexCount - 2; i++ )
					{
						int idx0 = strip.IndexOffset + i;
						int idx1 = strip.IndexOffset + i + 1;
						int idx2 = strip.IndexOffset + i + 2;

						// Validate all indices are in bounds
						if ( idx0 < 0 || idx0 >= indicesLength ||
						     idx1 < 0 || idx1 >= indicesLength ||
						     idx2 < 0 || idx2 >= indicesLength )
							continue;
						
						int v0 = stripGroup.Indices[idx0];
						int v1 = stripGroup.Indices[idx1];
						int v2 = stripGroup.Indices[idx2];
						
						totalTriangles++;
						
						// Validate vertex indices are within the strip group's vertex range
						if ( v0 < 0 || v0 >= stripGroupVertexCount ||
						     v1 < 0 || v1 >= stripGroupVertexCount ||
						     v2 < 0 || v2 >= stripGroupVertexCount )
						{
							totalSkippedTriangles++;
							continue;
						}

						if ( i % 2 == 0 )
						{
							// Even triangles: reverse winding
							meshIndices.Add( baseVertex + v0 );
							meshIndices.Add( baseVertex + v2 );
							meshIndices.Add( baseVertex + v1 );
						}
						else
						{
							// Odd triangles: already flipped, so keep original
							meshIndices.Add( baseVertex + v0 );
							meshIndices.Add( baseVertex + v1 );
							meshIndices.Add( baseVertex + v2 );
						}
					}
				}
			}
		}

		if ( meshVertices.Count == 0 || meshIndices.Count == 0 )
		{
			Log.Warning( $"BuildMesh: Empty mesh - vertices={meshVertices.Count}, indices={meshIndices.Count}" );
			return null;
		}

		// Log mesh stats
		Log.Info( $"BuildMesh '{meshName}': {meshVertices.Count} vertices, {meshIndices.Count / 3} triangles (from {totalTriangles} processed, {totalSkippedTriangles} skipped)" );
		
		// Create s&box mesh with meaningful name for body group display
		var mesh = new Mesh( meshName, material );
		mesh.Bounds = bounds;
		mesh.CreateVertexBuffer<SkinnedVertex>( meshVertices.Count, SkinnedVertex.Layout, meshVertices.ToArray() );
		mesh.CreateIndexBuffer( meshIndices.Count, meshIndices.ToArray() );

		return mesh;
	}

	/// <summary>
	/// Create an empty/invisible mesh for registering empty body group choices.
	/// This creates a degenerate triangle that won't render but allows the choice to exist.
	/// </summary>
	private static Mesh CreateEmptyMesh( string name )
	{
		try
		{
			var mesh = new Mesh( name, DefaultMaterial );
			
			// Create a single degenerate triangle at origin (all vertices at same point)
			// This won't render anything visible but registers the mesh
			var vertices = new SkinnedVertex[3];
			for ( int i = 0; i < 3; i++ )
			{
				vertices[i] = new SkinnedVertex
				{
					Position = Vector3.Zero,
					Normal = Vector3.Up,
					Tangent = Vector3.Forward,
					TexCoord = Vector2.Zero,
					BlendIndices = new Color32( 0, 0, 0, 0 ),
					BlendWeights = new Color32( 255, 0, 0, 0 )
				};
			}
			
			var indices = new int[] { 0, 1, 2 };
			
			mesh.Bounds = new BBox( Vector3.Zero, Vector3.One );
			mesh.CreateVertexBuffer<SkinnedVertex>( 3, SkinnedVertex.Layout, vertices );
			mesh.CreateIndexBuffer( 3, indices );
			
			return mesh;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Add attachments to the model builder.
	/// </summary>
	private static void AddAttachments( ModelBuilder builder, SourceModel model )
	{
		foreach ( var attachment in model.Mdl.Attachments )
		{
			string boneName = attachment.BoneIndex >= 0 && attachment.BoneIndex < model.Mdl.Bones.Count
				? model.Mdl.Bones[attachment.BoneIndex].Name
				: null;

			// Extract position and rotation from the 3x4 matrix
			var matrix = attachment.Matrix;
			Vector3 position = ConvertPosition( new Vector3( matrix[9], matrix[10], matrix[11] ) );
			
			// Extract rotation from the matrix columns
			Vector3 forward = new Vector3( matrix[0], matrix[1], matrix[2] );
			Vector3 left = new Vector3( matrix[3], matrix[4], matrix[5] );
			Vector3 up = new Vector3( matrix[6], matrix[7], matrix[8] );

			Rotation rotation = Rotation.LookAt( ConvertDirection( forward ), ConvertDirection( up ) );

			builder.AddAttachment( attachment.Name, position, rotation, boneName );
		}
	}

	/// <summary>
	/// Convert Source position to s&box coordinate system.
	/// Source: X right, Y forward, Z up
	/// s&box: X forward, Y left, Z up
	/// </summary>
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	private static Vector3 ConvertPosition( Vector3 v )
	{
		return new Vector3( v.y, -v.x, v.z ) * SourceConstants.SCALE;
	}

	/// <summary>
	/// Convert Source direction vector to s&box coordinate system.
	/// </summary>
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	private static Vector3 ConvertDirection( Vector3 v )
	{
		return new Vector3( v.y, -v.x, v.z );
	}

	/// <summary>
	/// Convert Source quaternion to s&box rotation.
	/// </summary>
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	private static Rotation ConvertRotation( Quaternion q )
	{
		// System.Numerics.Quaternion uses uppercase X, Y, Z, W
		return new Rotation( q.Y, -q.X, q.Z, q.W );
	}

	/// <summary>
	/// Normalize bone weights to Color32 (0-255 range).
	/// </summary>
	private static Color32 NormalizeWeights( float w0, float w1, float w2 )
	{
		float w3 = 1f - w0 - w1 - w2;
		if ( w3 < 0 ) w3 = 0;

		int iw0 = (int)(w0 * 255f + 0.5f);
		int iw1 = (int)(w1 * 255f + 0.5f);
		int iw2 = (int)(w2 * 255f + 0.5f);
		int iw3 = (int)(w3 * 255f + 0.5f);

		// Ensure weights sum to 255
		int total = iw0 + iw1 + iw2 + iw3;
		int diff = 255 - total;
		if ( diff != 0 )
		{
			// Add difference to the largest weight
			int max = Math.Max( Math.Max( iw0, iw1 ), Math.Max( iw2, iw3 ) );
			if ( iw0 == max ) iw0 += diff;
			else if ( iw1 == max ) iw1 += diff;
			else if ( iw2 == max ) iw2 += diff;
			else iw3 += diff;
		}

		return new Color32( (byte)iw0, (byte)iw1, (byte)iw2, (byte)iw3 );
	}

	/// <summary>
	/// Add physics collision shapes and ragdoll constraints from PHY data.
	/// </summary>
	private static void AddPhysics( ModelBuilder builder, SourceModel model )
	{
		if ( !model.HasPhysics )
			return;

		var phy = model.Phy;
		bool isRagdoll = phy.RagdollConstraints.Count > 0;
		
		Log.Info( $"AddPhysics: {phy.CollisionData.Count} solids, {phy.RagdollConstraints.Count} constraints, ragdoll={isRagdoll}" );

		// Build bone name -> index mapping and calculate bone transforms
		// Use the SAME coordinate conversion as BuildBones for consistency
		var boneNameToIndex = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		var boneTransforms = new Transform[model.Mdl.Bones.Count];
		
		for ( int i = 0; i < model.Mdl.Bones.Count; i++ )
		{
			var bone = model.Mdl.Bones[i];
			boneNameToIndex[bone.Name] = i;
			
			// Convert from Source to s&box coordinate system (same as BuildBones)
			Vector3 position = ConvertPosition( bone.Position );
			Rotation rotation = ConvertRotation( bone.Quaternion );
			
			var localTransform = new Transform( position, rotation );
			boneTransforms[i] = bone.ParentIndex >= 0 ? boneTransforms[bone.ParentIndex].ToWorld( localTransform ) : localTransform;
		}

		// For ragdolls, we need to create physics bodies and joints
		// For static/animated models, we just add collision hulls
		
		if ( isRagdoll )
		{
			// Create physics bodies for each solid
			var bodyBuilders = new Dictionary<int, (PhysicsBodyBuilder builder, int index)>();
			var solidToBoneIndex = new Dictionary<int, int>();
			int bodyIndex = 0;
			
			for ( int solidIdx = 0; solidIdx < phy.CollisionData.Count; solidIdx++ )
			{
				var collision = phy.CollisionData[solidIdx];
				
				// Get physics properties for this solid
				PhySolid solidProps = solidIdx < phy.Solids.Count ? phy.Solids[solidIdx] : null;
				string boneName = solidProps?.Name ?? "";
				float mass = solidProps?.Mass ?? 10f;
				
				// Create a physics body for this solid
				var bodyBuilder = builder.AddBody( mass );
				if ( !string.IsNullOrEmpty( boneName ) )
				{
					bodyBuilder.BoneName = boneName;
					
					// Track bone index for joint frame calculation
					if ( boneNameToIndex.TryGetValue( boneName, out int boneIdx ) )
					{
						solidToBoneIndex[solidIdx] = boneIdx;
					}
				}
				
				bodyBuilders[solidIdx] = (bodyBuilder, bodyIndex++);
				
				// Add each convex mesh to the body
				int hullIdx = 0;
				foreach ( var mesh in collision.ConvexMeshes )
				{
					if ( mesh.Vertices == null || mesh.Vertices.Count < 4 )
						continue;

					try
					{
						// Convert hull vertices from Source to s&box coordinate system
						// This matches the bone coordinate conversion we do
						var convertedVerts = new Vector3[mesh.Vertices.Count];
						for ( int v = 0; v < mesh.Vertices.Count; v++ )
						{
							convertedVerts[v] = ConvertPosition( mesh.Vertices[v] );
						}
						
						// Use hull simplification like HL2 does - important for complex hulls like the head
						bodyBuilder.AddHull( convertedVerts, Transform.Zero, new PhysicsBodyBuilder.HullSimplify
						{
							Method = PhysicsBodyBuilder.SimplifyMethod.QEM
						} );
						hullIdx++;
					}
					catch ( Exception ex )
					{
						Log.Warning( $"AddPhysics: Failed to add hull: {ex.Message}" );
					}
				}
			}
			
			// Add ragdoll joints
			foreach ( var constraint in phy.RagdollConstraints )
			{
				if ( !bodyBuilders.TryGetValue( constraint.ParentIndex, out var parentBody ) ||
				     !bodyBuilders.TryGetValue( constraint.ChildIndex, out var childBody ) )
				{
					continue;
				}

				if ( parentBody.index == childBody.index )
					continue;

				try
				{
					// Calculate joint frames like HL2 does
					Transform frame1 = Transform.Zero;
					Transform frame2 = Transform.Zero;
					
					bool hasParentBone = solidToBoneIndex.TryGetValue( constraint.ParentIndex, out int parentBoneIdx );
					bool hasChildBone = solidToBoneIndex.TryGetValue( constraint.ChildIndex, out int childBoneIdx );
					
					if ( hasParentBone && hasChildBone )
					{
						var parentBoneWorld = boneTransforms[parentBoneIdx];
						var childBoneWorld = boneTransforms[childBoneIdx];
						var childPosInParent = parentBoneWorld.PointToLocal( childBoneWorld.Position );
						var childRotInParent = parentBoneWorld.RotationToLocal( childBoneWorld.Rotation );
						
						frame1 = new Transform( childPosInParent, childRotInParent );
						frame2 = Transform.Zero;
					}
					
					// HL2 uses X for twist, Y/Z for swing (like Source engine)
					float twistMin = constraint.XMin;
					float twistMax = constraint.XMax;
					
					float twistRange = MathF.Abs( twistMax - twistMin );
					float swingYRange = MathF.Abs( constraint.YMax - constraint.YMin );
					float swingZRange = MathF.Abs( constraint.ZMax - constraint.ZMin );
					
					// Threshold for meaningful motion
					const float DofThreshold = 5f;
					int dofCount = 0;
					int dofMask = 0;
					if ( twistRange > DofThreshold ) { dofCount++; dofMask |= 1; }
					if ( swingYRange > DofThreshold ) { dofCount++; dofMask |= 2; }
					if ( swingZRange > DofThreshold ) { dofCount++; dofMask |= 4; }

					if ( dofCount == 0 )
					{
						// Fixed joint - no meaningful motion
						builder.AddFixedJoint( parentBody.index, childBody.index, frame1, frame2 );
					}
					else if ( dofCount == 1 )
					{
						// Hinge joint - only one axis has meaningful range
						float hingeMin, hingeMax;
						if ( (dofMask & 1) != 0 ) // Twist has range
						{
							hingeMin = twistMin;
							hingeMax = twistMax;
						}
						else if ( (dofMask & 2) != 0 ) // Y swing has range
						{
							hingeMin = constraint.YMin;
							hingeMax = constraint.YMax;
						}
						else // Z swing has range
						{
							hingeMin = constraint.ZMin;
							hingeMax = constraint.ZMax;
						}
						
						builder.AddHingeJoint( parentBody.index, childBody.index, frame1, frame2 )
							.WithTwistLimit( hingeMin, hingeMax );
					}
					else
					{
						// Ball joint - multiple axes with meaningful range
						float swingY = MathF.Max( MathF.Abs( constraint.YMin ), MathF.Abs( constraint.YMax ) );
						float swingZ = MathF.Max( MathF.Abs( constraint.ZMin ), MathF.Abs( constraint.ZMax ) );
						float swingLimit = MathF.Max( swingY, swingZ );
						
						builder.AddBallJoint( parentBody.index, childBody.index, frame1, frame2 )
							.WithSwingLimit( swingLimit )
							.WithTwistLimit( twistMin, twistMax );
					}
				}
				catch ( Exception ex )
				{
					Log.Warning( $"  Joint FAILED {constraint.ParentIndex}->{constraint.ChildIndex}: {ex.Message}" );
				}
			}
		}
		else
		{
			// For non-ragdoll models, just add collision hulls
			for ( int solidIdx = 0; solidIdx < phy.CollisionData.Count; solidIdx++ )
			{
				var collision = phy.CollisionData[solidIdx];
				
				foreach ( var mesh in collision.ConvexMeshes )
				{
					if ( mesh.Vertices == null || mesh.Vertices.Count < 4 )
						continue;

					try
					{
						// Vertices are already in Source coordinates (IVP to Source conversion done in PhyFile)
						builder.AddCollisionHull( mesh.Vertices );
					}
					catch ( Exception ex )
					{
						Log.Warning( $"AddPhysics: Failed to add hull for solid {solidIdx}: {ex.Message}" );
					}
				}
			}
		}
	}

	/// <summary>
	/// Convert Source physics position to s&box coordinate system.
	/// Physics uses inches, s&box uses inches but different axes.
	/// </summary>
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	private static Vector3 ConvertPhysicsPosition( Vector3 v )
	{
		// Source physics: X right, Y forward, Z up
		// s&box: X forward, Y left, Z up
		return new Vector3( v.y, -v.x, v.z ) * SourceConstants.SCALE;
	}
}
