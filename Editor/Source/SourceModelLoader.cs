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
	/// <param name="skinFamily">Skin family index for material replacement</param>
	/// <param name="path">Resource path for the model</param>
	/// <param name="mount">Mount to load materials from</param>
	/// <returns>s&box Model</returns>
	public static Model Convert( SourceModel sourceModel, int lod = 0, int skinFamily = 0, string path = null, GModMount mount = null )
	{
		var builder = Model.Builder;

		if ( !string.IsNullOrEmpty( path ) )
			builder.WithName( path );

		// Build material list with skin replacement
		var materials = BuildMaterialList( sourceModel, skinFamily, mount );

		// Add bones
		AddBones( builder, sourceModel );

		// Add meshes from each body part
		AddMeshes( builder, sourceModel, materials, lod );

		// Add attachments
		AddAttachments( builder, sourceModel );

		// Add physics collision and ragdoll constraints
		AddPhysics( builder, sourceModel );

		return builder.Create();
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

			// Get material name
			string materialName = materialIndex < model.Mdl.Materials.Count
				? model.Mdl.Materials[materialIndex]
				: model.Mdl.Materials[i];

			// Try to load the material from the mount
			Material material = null;
			bool foundMaterial = false;
			if ( mount != null && !string.IsNullOrEmpty( materialName ) )
			{
				// Search in material directories - check if VMT exists before loading
				foreach ( var matDir in model.Mdl.MaterialPaths )
				{
					var matPath = $"materials/{matDir}{materialName}".Replace( "\\", "/" ).Replace( "//", "/" ).TrimStart( '/' );
					var vmtPath = $"{matPath}.vmt";
					var mountPath = $"mount://garrysmod/{matPath}.vmat";
					
					// Only try to load if the VMT file actually exists in the VPK
					if ( mount.FileExists( vmtPath ) )
					{
						material = Material.Load( mountPath );
						if ( material != null && material.IsValid() )
						{
							foundMaterial = true;
							break;
						}
					}
				}

				// Try without material path prefix
				if ( !foundMaterial )
				{
					var matPath = $"materials/{materialName}".Replace( "\\", "/" ).TrimStart( '/' );
					var vmtPath = $"{matPath}.vmt";
					var mountPath = $"mount://garrysmod/{matPath}.vmat";
					
					if ( mount.FileExists( vmtPath ) )
					{
						material = Material.Load( mountPath );
						if ( material != null && material.IsValid() )
						{
							foundMaterial = true;
						}
					}
				}
				
				if ( !foundMaterial )
				{
					Log.Warning( $"Material not found: '{materialName}'" );
				}
			}

			materials.Add( material ?? DefaultMaterial );
		}

		return materials;
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
	private static void AddMeshes( ModelBuilder builder, SourceModel model, List<Material> materials, int lod )
	{
		var vvd = model.Vvd;
		var vtx = model.Vtx;
		var mdl = model.Mdl;

		// Get vertices for this LOD
		var vertices = vvd.GetVerticesForLod( lod );
		var tangents = vvd.Tangents;

		// Track cumulative vertex count across all models (NOT mdlModel.VertexIndex!)
		// This is how Crowbar calculates bodyPartVertexIndexStart
		int bodyPartVertexIndexStart = 0;

		// Process each body part
		for ( int bpIdx = 0; bpIdx < mdl.BodyParts.Count && bpIdx < vtx.BodyParts.Count; bpIdx++ )
		{
			var mdlBodyPart = mdl.BodyParts[bpIdx];
			var vtxBodyPart = vtx.BodyParts[bpIdx];

			// Process each model in the body part
			for ( int modelIdx = 0; modelIdx < mdlBodyPart.Models.Count && modelIdx < vtxBodyPart.Models.Count; modelIdx++ )
			{
				var mdlModel = mdlBodyPart.Models[modelIdx];
				var vtxModel = vtxBodyPart.Models[modelIdx];

				// Skip blank/empty models
				if ( string.IsNullOrEmpty( mdlModel.Name ) || mdlModel.Name.StartsWith( "blank" ) )
					continue;

				// Get the LOD data (use requested LOD, or fall back to 0)
				int lodIdx = Math.Min( lod, vtxModel.Lods.Count - 1 );
				if ( lodIdx < 0 ) continue;

				var vtxLod = vtxModel.Lods[lodIdx];

				// Process each mesh
				for ( int meshIdx = 0; meshIdx < mdlModel.Meshes.Count && meshIdx < vtxLod.Meshes.Count; meshIdx++ )
				{
					var mdlMesh = mdlModel.Meshes[meshIdx];
					var vtxMesh = vtxLod.Meshes[meshIdx];

					// Get material
					var material = mdlMesh.MaterialIndex < materials.Count
						? materials[mdlMesh.MaterialIndex]
						: DefaultMaterial;

					// Build mesh from strip groups
					// Pass the cumulative vertex start index for proper VVD indexing
					var mesh = BuildMesh(
						model,
						mdlMesh,
						vtxMesh,
						vertices,
						tangents,
						material,
						bodyPartVertexIndexStart
					);

					if ( mesh != null )
					{
						builder.AddMesh( mesh );
					}
				}

				// Add this model's vertex count to the cumulative total
				bodyPartVertexIndexStart += mdlModel.VertexCount;
			}
		}
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
		int bodyPartVertexIndexStart )
	{
		var meshVertices = new List<SkinnedVertex>();
		var meshIndices = new List<int>();
		var bounds = new BBox();

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
					// Add a default vertex to maintain index alignment
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
			foreach ( var strip in stripGroup.Strips )
			{
				if ( strip.IsTriList )
				{
					// Triangle list - reverse winding (0,1,2) -> (0,2,1)
					for ( int i = 0; i < strip.IndexCount; i += 3 )
					{
						int idx0 = strip.IndexOffset + i;
						int idx1 = strip.IndexOffset + i + 1;
						int idx2 = strip.IndexOffset + i + 2;
						
						if ( idx2 >= stripGroup.Indices.Length )
							continue;
						
						int v0 = stripGroup.Indices[idx0];
						int v1 = stripGroup.Indices[idx1];
						int v2 = stripGroup.Indices[idx2];
						
						// Validate vertex indices are within the strip group's vertex range
						if ( v0 >= stripGroupVertexCount || v1 >= stripGroupVertexCount || v2 >= stripGroupVertexCount )
							continue;
						
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

						if ( idx2 >= stripGroup.Indices.Length )
							continue;
						
						int v0 = stripGroup.Indices[idx0];
						int v1 = stripGroup.Indices[idx1];
						int v2 = stripGroup.Indices[idx2];
						
						// Validate vertex indices are within the strip group's vertex range
						if ( v0 >= stripGroupVertexCount || v1 >= stripGroupVertexCount || v2 >= stripGroupVertexCount )
							continue;

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

		// Create s&box mesh
		var mesh = new Mesh( material );
		mesh.Bounds = bounds;
		mesh.CreateVertexBuffer<SkinnedVertex>( meshVertices.Count, SkinnedVertex.Layout, meshVertices.ToArray() );
		mesh.CreateIndexBuffer( meshIndices.Count, meshIndices.ToArray() );

		return mesh;
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
