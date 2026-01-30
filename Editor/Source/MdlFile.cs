namespace GModMount.Source;

/// <summary>
/// Parsed MDL file data.
/// </summary>
public class MdlFile
{
	public StudioHeader Header { get; private set; }
	public StudioHeader2? Header2 { get; private set; }
	public int Version => Header.Version;

	/// <summary>
	/// Model name extracted from header.
	/// </summary>
	public string Name { get; private set; }

	// Bones
	public List<MdlBone> Bones { get; } = new();

	// Body parts and models
	public List<MdlBodyPart> BodyParts { get; } = new();

	// Materials
	public List<string> Materials { get; } = new();
	public List<string> MaterialPaths { get; } = new();

	// Skin families (material replacements)
	public List<short[]> SkinFamilies { get; } = new();

	// Attachments
	public List<MdlAttachment> Attachments { get; } = new();

	// Hitboxes
	public List<MdlHitboxSet> HitboxSets { get; } = new();

	/// <summary>
	/// Load an MDL file from a byte array.
	/// </summary>
	public static MdlFile Load( byte[] data )
	{
		using var stream = new MemoryStream( data );
		using var reader = new BinaryReader( stream );
		return Load( reader );
	}

	/// <summary>
	/// Load an MDL file from a stream.
	/// </summary>
	public static MdlFile Load( Stream stream )
	{
		using var reader = new BinaryReader( stream, Encoding.ASCII, leaveOpen: true );
		return Load( reader );
	}

	/// <summary>
	/// Load an MDL file from a binary reader.
	/// </summary>
	public static MdlFile Load( BinaryReader reader )
	{
		var mdl = new MdlFile();
		long fileLength = reader.BaseStream.Length;
		int headerSize = Marshal.SizeOf<StudioHeader>();

		// Check if file is large enough for header
		if ( fileLength < headerSize )
		{
			throw new InvalidDataException( $"MDL file too small: {fileLength} bytes, header needs {headerSize} bytes" );
		}

		// Read main header
		mdl.Header = reader.ReadStruct<StudioHeader>();

		if ( !mdl.Header.IsValid )
		{
			throw new InvalidDataException( $"Invalid MDL file: ID=0x{mdl.Header.Id:X8}" );
		}

		// Support MDL versions 44-49 and 53
		if ( mdl.Header.Version < SourceConstants.MDL_VERSION_44 ||
			 (mdl.Header.Version > SourceConstants.MDL_VERSION_49 && mdl.Header.Version != SourceConstants.MDL_VERSION_53) )
		{
			throw new InvalidDataException( $"Unsupported MDL version: {mdl.Header.Version}" );
		}

		// Read name from the file (at offset 12, after id/version/checksum, 64 bytes)
		reader.BaseStream.Position = 12;
		mdl.Name = ReadFixedString( reader.ReadBytes( 64 ), 64 );
		
		// Read secondary header if present and within bounds
		if ( mdl.Header.StudioHdr2Index > 0 && mdl.Header.StudioHdr2Index + Marshal.SizeOf<StudioHeader2>() <= fileLength )
		{
			reader.BaseStream.Position = mdl.Header.StudioHdr2Index;
			mdl.Header2 = reader.ReadStruct<StudioHeader2>();
		}

		// Read bones (with bounds checking)
		if ( mdl.Header.BoneCount > 0 && mdl.Header.BoneOffset > 0 && mdl.Header.BoneOffset < fileLength )
		{
			mdl.ReadBones( reader );
		}

		// Read body parts (with bounds checking)
		if ( mdl.Header.BodyPartCount > 0 && mdl.Header.BodyPartOffset > 0 && mdl.Header.BodyPartOffset < fileLength )
		{
			mdl.ReadBodyParts( reader );
		}

		// Read materials (with bounds checking)
		if ( mdl.Header.TextureCount > 0 && mdl.Header.TextureOffset > 0 && mdl.Header.TextureOffset < fileLength )
		{
			mdl.ReadMaterials( reader );
		}

		// Read skin families (with bounds checking)
		if ( mdl.Header.SkinFamilyCount > 0 && mdl.Header.SkinReferenceIndex > 0 && mdl.Header.SkinReferenceIndex < fileLength )
		{
			mdl.ReadSkinFamilies( reader );
		}

		// Read attachments (with bounds checking)
		if ( mdl.Header.AttachmentCount > 0 && mdl.Header.AttachmentOffset > 0 && mdl.Header.AttachmentOffset < fileLength )
		{
			mdl.ReadAttachments( reader );
		}

		// Read hitbox sets (with bounds checking)
		if ( mdl.Header.HitboxSetCount > 0 && mdl.Header.HitboxSetOffset > 0 && mdl.Header.HitboxSetOffset < fileLength )
		{
			mdl.ReadHitboxSets( reader );
		}

		Log.Info( $"MdlFile: Successfully parsed MDL" );
		return mdl;
	}

	/// <summary>
	/// Read a fixed-length string from a byte array.
	/// </summary>
	private static string ReadFixedString( byte[] bytes, int maxLength )
	{
		int length = 0;
		while ( length < maxLength && length < bytes.Length && bytes[length] != 0 )
		{
			length++;
		}
		return Encoding.ASCII.GetString( bytes, 0, length );
	}

	private void ReadBones( BinaryReader reader )
	{
		if ( Header.BoneCount <= 0 || Header.BoneOffset <= 0 )
			return;

		long fileLength = reader.BaseStream.Length;
		reader.BaseStream.Position = Header.BoneOffset;

		// Bone size is 216 bytes for v44+ (includes Unused[8] for all these versions)
		// Based on Crowbar's SourceMdlFile44.vb which reads unused[8] for v44
		int boneSize = 216;

		for ( int i = 0; i < Header.BoneCount; i++ )
		{
			long boneStart = reader.BaseStream.Position;
			
			// Check bounds
			if ( boneStart + boneSize > fileLength )
			{
				Log.Warning( $"MdlFile: Bone {i} would exceed file bounds ({boneStart}+{boneSize}>{fileLength}), stopping" );
				break;
			}

			// Read bone fields manually to handle version differences
			int nameOffset = reader.ReadInt32();
			int parentBone = reader.ReadInt32();
			reader.BaseStream.Position += 24; // Skip BoneController[6]
			var position = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var quaternion = new Quaternion( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var rotation = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			reader.BaseStream.Position += 24; // Skip PositionScale and RotationScale
			reader.BaseStream.Position += 48; // Skip PoseToBone[12]
			reader.BaseStream.Position += 16; // Skip AlignmentQuaternion
			int flags = reader.ReadInt32();
			reader.BaseStream.Position += 20; // Skip ProceduralType, ProceduralIndex, PhysicsBone, SurfacePropIndex, Contents
			
			// Skip Unused[8] only for v47+
			if ( Header.Version >= SourceConstants.MDL_VERSION_47 )
			{
				reader.BaseStream.Position += 32;
			}

			string name = reader.ReadStringAtOffset( boneStart, nameOffset );

			Bones.Add( new MdlBone
			{
				Index = i,
				Name = name,
				ParentIndex = parentBone,
				Position = position,
				Quaternion = quaternion,
				Rotation = rotation,
				Flags = flags
			} );
			
			// Seek to the next bone - IMPORTANT: ReadStringAtOffset changes the stream position
			reader.BaseStream.Position = boneStart + boneSize;
		}
		Log.Info( $"MdlFile: Read {Bones.Count} bones" );
	}

	private void ReadBodyParts( BinaryReader reader )
	{
		if ( Header.BodyPartCount <= 0 || Header.BodyPartOffset <= 0 )
			return;

		long fileLength = reader.BaseStream.Length;
		reader.BaseStream.Position = Header.BodyPartOffset;

		for ( int i = 0; i < Header.BodyPartCount; i++ )
		{
			long bodyPartStart = reader.BaseStream.Position;
			
			if ( bodyPartStart + 16 > fileLength )
			{
				Log.Warning( $"MdlFile.ReadBodyParts: bodyPart {i} would exceed file bounds" );
				break;
			}
			
			var bodyPart = reader.ReadStruct<StudioBodyPart>();

			string name = reader.ReadStringAtOffset( bodyPartStart, bodyPart.NameOffset );

			var mdlBodyPart = new MdlBodyPart
			{
				Index = i,
				Name = name,
				Base = bodyPart.Base
			};

			// Read models within this body part
			if ( bodyPart.ModelCount > 0 && bodyPart.ModelOffset != 0 )
			{
				long modelsStart = bodyPartStart + bodyPart.ModelOffset;
				reader.BaseStream.Position = modelsStart;

				for ( int j = 0; j < bodyPart.ModelCount; j++ )
				{
					long modelStart = reader.BaseStream.Position;
					
					if ( modelStart + 148 > fileLength )
					{
						Log.Warning( $"MdlFile.ReadBodyParts: model {j} would exceed file bounds" );
						break;
					}
					
					// Read model name first (64 bytes at start of struct)
					string modelName = ReadFixedString( reader.ReadBytes( 64 ), 64 );
					
					// Read rest of StudioModel struct manually
					int modelType = reader.ReadInt32();
					float boundingRadius = reader.ReadSingle();
					int meshCount = reader.ReadInt32();
					int meshOffset = reader.ReadInt32();
					int vertexCount = reader.ReadInt32();
					int vertexIndex = reader.ReadInt32();
					int tangentIndex = reader.ReadInt32();
					int attachmentCount = reader.ReadInt32();
					int attachmentOffset = reader.ReadInt32();
					int eyeballCount = reader.ReadInt32();
					int eyeballOffset = reader.ReadInt32();
					reader.ReadInt32(); // VertexDataPointer
					reader.ReadInt32(); // TangentDataPointer
					for ( int u = 0; u < 8; u++ ) reader.ReadInt32(); // Unused

					var mdlModel = new MdlModel
					{
						Index = j,
						Name = modelName,
						BoundingRadius = boundingRadius,
						VertexCount = vertexCount,
						VertexIndex = vertexIndex,
						TangentIndex = tangentIndex
					};

					// Read meshes within this model
					if ( meshCount > 0 && meshOffset != 0 )
					{
						long meshesStart = modelStart + meshOffset;
						reader.BaseStream.Position = meshesStart;

						for ( int k = 0; k < meshCount; k++ )
						{
							long meshStart = reader.BaseStream.Position;
							
							if ( meshStart + Marshal.SizeOf<StudioMesh>() > fileLength )
							{
								Log.Warning( $"MdlFile.ReadBodyParts: mesh {k} would exceed file bounds" );
								break;
							}
							
							var mesh = reader.ReadStruct<StudioMesh>();

							mdlModel.Meshes.Add( new MdlMesh
							{
								Index = k,
								MaterialIndex = mesh.Material,
								VertexCount = mesh.VertexCount,
								VertexOffset = mesh.VertexOffset,
								Center = mesh.Center
							} );
						}
					}

					mdlBodyPart.Models.Add( mdlModel );

					// Move to next model (148 bytes per model)
					reader.BaseStream.Position = modelStart + 148;
				}
			}

			BodyParts.Add( mdlBodyPart );

			// Move to next body part (16 bytes per body part)
			reader.BaseStream.Position = bodyPartStart + 16;
		}
		Log.Info( $"MdlFile: Read {BodyParts.Count} body parts" );
	}

	private void ReadMaterials( BinaryReader reader )
	{
		long fileLength = reader.BaseStream.Length;
		
		// Read texture names
		if ( Header.TextureCount > 0 && Header.TextureOffset > 0 )
		{
			reader.BaseStream.Position = Header.TextureOffset;
			int textureSize = Marshal.SizeOf<StudioTexture>();

			for ( int i = 0; i < Header.TextureCount; i++ )
			{
				long textureStart = reader.BaseStream.Position;
				
				if ( textureStart + textureSize > fileLength )
				{
					Log.Warning( $"MdlFile: texture {i} would exceed file bounds" );
					break;
				}
				
				var texture = reader.ReadStruct<StudioTexture>();

				string name = reader.ReadStringAtOffset( textureStart, texture.NameOffset );
				Materials.Add( name );
			}
		}

		// Read texture directories (paths)
		if ( Header.TextureDirCount > 0 && Header.TextureDirOffset > 0 )
		{
			reader.BaseStream.Position = Header.TextureDirOffset;

			for ( int i = 0; i < Header.TextureDirCount; i++ )
			{
				if ( reader.BaseStream.Position + 4 > fileLength )
				{
					Log.Warning( $"MdlFile: textureDir {i} would exceed file bounds" );
					break;
				}
				
				int offset = reader.ReadInt32();
				string path = reader.ReadStringAtOffset( 0, offset );
				MaterialPaths.Add( path );
			}
		}
		Log.Info( $"MdlFile: Read {Materials.Count} materials, {MaterialPaths.Count} paths" );
	}

	private void ReadSkinFamilies( BinaryReader reader )
	{
		if ( Header.SkinFamilyCount <= 0 || Header.SkinReferenceCount <= 0 || Header.SkinReferenceIndex <= 0 )
			return;

		long fileLength = reader.BaseStream.Length;
		reader.BaseStream.Position = Header.SkinReferenceIndex;
		
		int expectedSize = Header.SkinFamilyCount * Header.SkinReferenceCount * 2;
		
		if ( Header.SkinReferenceIndex + expectedSize > fileLength )
		{
			Log.Warning( $"MdlFile.ReadSkinFamilies: would exceed file bounds, skipping" );
			return;
		}

		for ( int i = 0; i < Header.SkinFamilyCount; i++ )
		{
			short[] family = new short[Header.SkinReferenceCount];
			for ( int j = 0; j < Header.SkinReferenceCount; j++ )
			{
				family[j] = reader.ReadInt16();
			}
			SkinFamilies.Add( family );
		}
		Log.Info( $"MdlFile: Read {SkinFamilies.Count} skin families" );
	}

	private void ReadAttachments( BinaryReader reader )
	{
		if ( Header.AttachmentCount <= 0 || Header.AttachmentOffset <= 0 )
			return;

		long fileLength = reader.BaseStream.Length;
		reader.BaseStream.Position = Header.AttachmentOffset;
		
		// Attachment size is 92 bytes for v44+ (includes Unused[8] for all these versions)
		// Based on Crowbar's SourceMdlFile44.vb which reads unused[8] for v44 attachments
		int attachmentSize = 92;

		for ( int i = 0; i < Header.AttachmentCount; i++ )
		{
			long attachStart = reader.BaseStream.Position;
			
			if ( attachStart + attachmentSize > fileLength )
			{
				Log.Warning( $"MdlFile.ReadAttachments: attachment {i} exceeds file bounds" );
				break;
			}

			// Read attachment fields manually
			int nameOffset = reader.ReadInt32();
			uint flags = reader.ReadUInt32();
			int localBone = reader.ReadInt32();
			
			// Read 3x4 matrix (12 floats)
			float[] matrix = new float[12];
			for ( int j = 0; j < 12; j++ )
			{
				matrix[j] = reader.ReadSingle();
			}
			
			// Skip Unused[8] for v47+
			if ( Header.Version >= SourceConstants.MDL_VERSION_47 )
			{
				reader.BaseStream.Position += 32;
			}

			string name = reader.ReadStringAtOffset( attachStart, nameOffset );

			Attachments.Add( new MdlAttachment
			{
				Index = i,
				Name = name,
				BoneIndex = localBone,
				Matrix = matrix
			} );
			
			// Seek to the next attachment - IMPORTANT: ReadStringAtOffset changes the stream position
			reader.BaseStream.Position = attachStart + attachmentSize;
		}
	}

	private void ReadHitboxSets( BinaryReader reader )
	{
		if ( Header.HitboxSetCount <= 0 || Header.HitboxSetOffset <= 0 )
			return;

		long fileLength = reader.BaseStream.Length;
		reader.BaseStream.Position = Header.HitboxSetOffset;

		// Hitbox size is 68 bytes for all versions (including v44 based on Crowbar)
		int hitboxSize = 68;

		for ( int i = 0; i < Header.HitboxSetCount; i++ )
		{
			long setStart = reader.BaseStream.Position;
			
			// Check if we can read the hitbox set header
			if ( setStart + 12 > fileLength )
			{
				Log.Warning( $"MdlFile: hitbox set {i} header would exceed file bounds" );
				break;
			}
				
			var set = reader.ReadStruct<StudioHitboxSet>();

			string name = reader.ReadStringAtOffset( setStart, set.NameOffset );

			var hitboxSet = new MdlHitboxSet
			{
				Index = i,
				Name = name
			};

			// Read hitboxes
			if ( set.HitboxCount > 0 && set.HitboxOffset != 0 )
			{
				long hitboxArrayStart = setStart + set.HitboxOffset;
				long hitboxArrayEnd = hitboxArrayStart + (set.HitboxCount * hitboxSize);
				
				// Check if hitbox array is within bounds
				if ( hitboxArrayEnd > fileLength )
				{
					Log.Warning( $"MdlFile: hitbox array would exceed file bounds, skipping hitboxes" );
				}
				else
				{
					reader.BaseStream.Position = hitboxArrayStart;

					for ( int j = 0; j < set.HitboxCount; j++ )
					{
						long hitboxStart = reader.BaseStream.Position;
						
						// Read hitbox fields manually 
						int bone = reader.ReadInt32();
						int group = reader.ReadInt32();
						var bbMin = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
						var bbMax = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
						int hitboxNameOffset = reader.ReadInt32();
						
						// Skip unused[8] - 32 bytes - present in all versions
						reader.BaseStream.Position += 32;

						string hitboxName;
						if ( hitboxNameOffset != 0 && hitboxNameOffset < 10000 ) // sanity check
						{
							hitboxName = reader.ReadStringAtOffset( hitboxStart, hitboxNameOffset );
						}
						else
						{
							hitboxName = $"hitbox_{j}";
						}

						hitboxSet.Hitboxes.Add( new MdlHitbox
						{
							Index = j,
							Name = hitboxName,
							BoneIndex = bone,
							Group = group,
							Min = bbMin,
							Max = bbMax
						} );
						
						// Seek to the next hitbox - IMPORTANT: ReadStringAtOffset changes the stream position
						reader.BaseStream.Position = hitboxStart + hitboxSize;
					}
				}
			}

			HitboxSets.Add( hitboxSet );

			// Move to next set
			reader.BaseStream.Position = setStart + 12; // StudioHitboxSet is always 12 bytes
		}
		Log.Info( $"MdlFile: Read {HitboxSets.Count} hitbox sets" );
	}
}

/// <summary>
/// Parsed bone data.
/// </summary>
public class MdlBone
{
	public int Index { get; set; }
	public string Name { get; set; }
	public int ParentIndex { get; set; }
	public Vector3 Position { get; set; }
	public Quaternion Quaternion { get; set; }
	public Vector3 Rotation { get; set; }
	public int Flags { get; set; }
}

/// <summary>
/// Parsed body part data.
/// </summary>
public class MdlBodyPart
{
	public int Index { get; set; }
	public string Name { get; set; }
	public int Base { get; set; }
	public List<MdlModel> Models { get; } = new();
}

/// <summary>
/// Parsed model data (within a body part).
/// </summary>
public class MdlModel
{
	public int Index { get; set; }
	public string Name { get; set; }
	public float BoundingRadius { get; set; }
	public int VertexCount { get; set; }
	public int VertexIndex { get; set; }
	public int TangentIndex { get; set; }
	public List<MdlMesh> Meshes { get; } = new();
}

/// <summary>
/// Parsed mesh data (within a model).
/// </summary>
public class MdlMesh
{
	public int Index { get; set; }
	public int MaterialIndex { get; set; }
	public int VertexCount { get; set; }
	public int VertexOffset { get; set; }
	public Vector3 Center { get; set; }
}

/// <summary>
/// Parsed attachment data.
/// </summary>
public class MdlAttachment
{
	public int Index { get; set; }
	public string Name { get; set; }
	public int BoneIndex { get; set; }
	public float[] Matrix { get; set; }
}

/// <summary>
/// Parsed hitbox set.
/// </summary>
public class MdlHitboxSet
{
	public int Index { get; set; }
	public string Name { get; set; }
	public List<MdlHitbox> Hitboxes { get; } = new();
}

/// <summary>
/// Parsed hitbox.
/// </summary>
public class MdlHitbox
{
	public int Index { get; set; }
	public string Name { get; set; }
	public int BoneIndex { get; set; }
	public int Group { get; set; }
	public Vector3 Min { get; set; }
	public Vector3 Max { get; set; }
}
