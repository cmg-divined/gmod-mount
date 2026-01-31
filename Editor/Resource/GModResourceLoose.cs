using GModMount.Source;

namespace GModMount;

/// <summary>
/// Resource loader for loose model files (.mdl)
/// </summary>
internal class GModModelLoose : ResourceLoader<GModMount>
{
	private readonly GModMount _mount;
	private readonly string _filePath;
	private readonly string _rootPath;

	public GModModelLoose( GModMount mount, string filePath, string rootPath )
	{
		_mount = mount;
		_filePath = filePath;
		_rootPath = rootPath;
	}

	protected override object Load()
	{
		var basePath = _filePath[..^4]; // Remove .mdl
		
		try
		{
			// Read MDL data
			var mdlData = File.ReadAllBytes( _filePath );
			if ( mdlData == null || mdlData.Length < 408 )
				return Model.Error;

			// Read VVD
			var vvdPath = basePath + ".vvd";
			if ( !File.Exists( vvdPath ) )
				return Model.Error;
			var vvdData = File.ReadAllBytes( vvdPath );

			// Read VTX
			byte[] vtxData = null;
			string[] vtxExtensions = new string[] { ".dx90.vtx", ".dx80.vtx", ".sw.vtx" };
			foreach ( var ext in vtxExtensions )
			{
				var vtxPath = basePath + ext;
				if ( File.Exists( vtxPath ) )
				{
					vtxData = File.ReadAllBytes( vtxPath );
					break;
				}
			}
			if ( vtxData == null )
				return Model.Error;

			// Read PHY (optional)
			byte[] phyData = null;
			var phyPath = basePath + ".phy";
			if ( File.Exists( phyPath ) )
				phyData = File.ReadAllBytes( phyPath );

			// Parse and convert
			Log.Info( $"GModModelLoose: Loading model from {_filePath}" );
			var sourceModel = SourceModel.Load( mdlData, vvdData, vtxData, phyData );
			Log.Info( $"GModModelLoose: MDL has {sourceModel.Mdl.Eyeballs.Count} eyeballs" );
			
			// Log eyeball details
			foreach ( var eyeball in sourceModel.Mdl.Eyeballs )
			{
				Log.Info( $"GModModelLoose: Eyeball '{eyeball.Name}' tex={eyeball.TextureIndex} bone={eyeball.BoneIndex}" );
				Log.Info( $"  Origin=({eyeball.Origin.x:F3}, {eyeball.Origin.y:F3}, {eyeball.Origin.z:F3})" );
				Log.Info( $"  Radius={eyeball.Radius:F3}, IrisScale={eyeball.IrisScale:F3}" );
				Log.Info( $"  Up=({eyeball.Up.x:F3}, {eyeball.Up.y:F3}, {eyeball.Up.z:F3})" );
				Log.Info( $"  Forward=({eyeball.Forward.x:F3}, {eyeball.Forward.y:F3}, {eyeball.Forward.z:F3})" );
			}
			
			var result = SourceModelLoader.Convert( sourceModel, path: Path, mount: _mount );
			
			// Update eye materials with projection data (workaround since SourceModelLoader isn't hot-reloading)
			UpdateEyeMaterialsWorkaround( sourceModel, result );
			
			Log.Info( $"GModModelLoose: Convert complete" );
			return result;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"GModModelLoose: Failed to load {_filePath}: {ex.Message}" );
			return Model.Error;
		}
	}
	
	/// <summary>
	/// Update eye materials with eyeball projection data.
	/// Workaround since SourceModelLoader isn't hot-reloading.
	/// </summary>
	private void UpdateEyeMaterialsWorkaround( SourceModel model, Model result )
	{
		if ( model.Mdl.Eyeballs == null || model.Mdl.Eyeballs.Count == 0 )
			return;
		
		Log.Info( $"GModModelLoose: Updating {model.Mdl.Eyeballs.Count} eyeball materials" );
		
		// Get the materials from the result model
		var materials = result.Materials;
		if ( materials == null || materials.Length == 0 )
		{
			Log.Warning( "GModModelLoose: No materials on result model" );
			return;
		}
		
		// Calculate bone world transforms for eye positioning
		var boneTransforms = new Transform[model.Mdl.Bones.Count];
		for ( int i = 0; i < model.Mdl.Bones.Count; i++ )
		{
			var bone = model.Mdl.Bones[i];
			Vector3 position = SourceModelLoader.ConvertPosition( bone.Position );
			Rotation rotation = SourceModelLoader.ConvertRotation( bone.Quaternion );
			var localTransform = new Transform( position, rotation, 1f );
			
			if ( bone.ParentIndex >= 0 && bone.ParentIndex < i )
				localTransform = boneTransforms[bone.ParentIndex].ToWorld( localTransform );
			
			boneTransforms[i] = localTransform;
		}
		
		foreach ( var eyeball in model.Mdl.Eyeballs )
		{
			Log.Info( $"  Processing eyeball '{eyeball.Name}' tex={eyeball.TextureIndex}" );
			
			// Get the material for this eyeball
			if ( eyeball.TextureIndex < 0 || eyeball.TextureIndex >= materials.Length )
			{
				Log.Warning( $"    TextureIndex {eyeball.TextureIndex} out of range (materials.Length={materials.Length})" );
				continue;
			}
			
			var material = materials[eyeball.TextureIndex];
			if ( material == null || !material.IsValid() )
			{
				Log.Warning( $"    Material at index {eyeball.TextureIndex} is null or invalid" );
				continue;
			}
			
			Log.Info( $"    Material: {material.Name}" );
			
			// Get the bone transform for this eyeball
			Transform boneTransform = Transform.Zero;
			if ( eyeball.BoneIndex >= 0 && eyeball.BoneIndex < boneTransforms.Length )
				boneTransform = boneTransforms[eyeball.BoneIndex];
			
			// Convert eye origin from Source to s&box coordinates
			Vector3 eyeOriginLocal = SourceModelLoader.ConvertPosition( eyeball.Origin );
			Vector3 eyeOrigin = boneTransform.PointToWorld( eyeOriginLocal );
			
			// Radius in s&box units
			float radius = eyeball.Radius * SourceConstants.SCALE;
			
			// Scale factor - how much UV changes per world unit
			// Smaller scale = larger iris on texture, larger scale = smaller iris
			float scale = 1.0f / ( radius * 2.0f );
			
			Log.Info( $"    Eye origin: {eyeOrigin}, radius: {radius}, scale: {scale}" );
			
			// Simple projection: map world position relative to eye origin to UV
			// In s&box: +Y is right, +Z is up
			// We want the iris centered at UV (0.5, 0.5) when looking at the eye origin
			
			// ProjectionU: right direction (+Y in s&box)
			// UV.x = worldPos.y * scale - eyeOrigin.y * scale + 0.5
			Vector4 irisProjectionU = new Vector4( 0, scale, 0, -eyeOrigin.y * scale + 0.5f );
			
			// ProjectionV: up direction (+Z in s&box)
			// UV.y = worldPos.z * scale - eyeOrigin.z * scale + 0.5
			Vector4 irisProjectionV = new Vector4( 0, 0, scale, -eyeOrigin.z * scale + 0.5f );
			
			// Set material properties
			material.Set( "g_vEyeOrigin", eyeOrigin );
			material.Set( "g_vIrisProjectionU", irisProjectionU );
			material.Set( "g_vIrisProjectionV", irisProjectionV );
			
			Log.Info( $"    ProjectionU: {irisProjectionU}" );
			Log.Info( $"    ProjectionV: {irisProjectionV}" );
		}
	}
}

/// <summary>
/// Resource loader for loose texture files (.vtf)
/// </summary>
internal class GModTextureLoose : ResourceLoader<GModMount>
{
	private readonly GModMount _mount;
	private readonly string _filePath;

	public GModTextureLoose( GModMount mount, string filePath )
	{
		_mount = mount;
		_filePath = filePath;
	}

	protected override object Load()
	{
		try
		{
			var data = File.ReadAllBytes( _filePath );
			if ( data == null )
				return Texture.Invalid;

			var vtf = VtfFile.Load( data );
			var rgbaData = vtf.ConvertToRGBA();
			if ( rgbaData == null )
				return Texture.Invalid;

			return Texture.Create( vtf.Width, vtf.Height )
				.WithData( rgbaData )
				.WithMips()
				.Finish();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"GModTextureLoose: Failed to load {_filePath}: {ex.Message}" );
			return Texture.Invalid;
		}
	}
}

/// <summary>
/// Resource loader for loose material files (.vmt) with pseudo-PBR support.
/// </summary>
internal class GModMaterialLoose : ResourceLoader<GModMount>
{
	private readonly GModMount _mount;
	private readonly string _filePath;
	private readonly string _rootPath;

	public GModMaterialLoose( GModMount mount, string filePath, string rootPath )
	{
		_mount = mount;
		_filePath = filePath;
		_rootPath = rootPath;
	}

	private static Texture _defaultNormal;
	private static Texture _defaultRoughness;
	private static Texture _defaultAo;

	private static Texture DefaultNormal => _defaultNormal ??= Texture.Create( 1, 1 ).WithData( new byte[] { 128, 128, 255, 255 } ).Finish();
	private static Texture DefaultRoughness => _defaultRoughness ??= Texture.Create( 1, 1 ).WithData( new byte[] { 255, 255, 255, 255 } ).Finish();
	private static Texture DefaultAo => _defaultAo ??= Texture.Create( 1, 1 ).WithData( new byte[] { 255, 255, 255, 255 } ).Finish();

	protected override object Load()
	{
		try
		{
			var data = File.ReadAllBytes( _filePath );
			if ( data == null )
				return null;

			string vmtContent = System.Text.Encoding.UTF8.GetString( data );
			var vmt = VmtFile.Load( vmtContent );

			// Detect format and extract PBR properties (pass path for path-based detection)
			var pbrProps = PseudoPbrFormats.ExtractProperties( vmt, _filePath );
			
			// Log detected format
			Log.Info( $"GModMaterialLoose: {_filePath} -> {pbrProps.Format} (shader: {vmt.Shader})" );

			// Create material based on detected format
			var relativePath = System.IO.Path.GetRelativePath( _rootPath, _filePath ).Replace( '\\', '/' );
			var materialName = relativePath.Replace( ".vmt", "" );
			
			// Check for eye shader first (special handling)
			if ( pbrProps.IsEyeShader )
			{
				Log.Info( $"    Eye shader detected" );
				return CreateEyeMaterial( materialName, pbrProps );
			}
			
			return pbrProps.Format switch
			{
				PbrFormat.ExoPBR => CreateExoPbrMaterial( materialName, pbrProps ),
				PbrFormat.GPBR => CreateGpbrMaterial( materialName, pbrProps ),
				PbrFormat.MWBPBR => CreateMwbMaterial( materialName, pbrProps ),
				PbrFormat.BFTPseudoPBR => CreateBftMaterial( materialName, pbrProps ),
				PbrFormat.MadIvan18 => CreateMadIvan18Material( materialName, pbrProps ),
				_ => CreateSourceMaterial( materialName, pbrProps )
			};
		}
		catch ( Exception ex )
		{
			Log.Warning( $"GModMaterialLoose: Failed to load {_filePath}: {ex.Message}" );
			return null;
		}
	}

	private Material CreateExoPbrMaterial( string name, ExtractedPbrProperties props )
	{
		var material = Material.Create( name, "shaders/gmod_pbr.shader" );
		material.Set( "g_tColor", LoadTexture( props.BaseTexturePath, forceOpaqueAlpha: true ) ?? Texture.White );

		Texture roughnessTexture = DefaultRoughness;
		if ( !string.IsNullOrEmpty( props.ArmTexturePath ) )
		{
			var armData = LoadTextureRaw( props.ArmTexturePath );
			if ( armData != null )
			{
				var vtf = VtfFile.Load( armData );
				var rgba = vtf.ConvertToRGBA();
				if ( rgba != null )
					roughnessTexture = CreateTexture( PbrTextureGenerator.ExtractRoughnessFromArm( rgba, vtf.Width, vtf.Height ), vtf.Width, vtf.Height );
			}
		}

		material.Set( "g_tRoughness", roughnessTexture );
		material.Set( "g_tNormal", LoadNormalMap( props.ExoNormalPath ?? props.BumpMapPath ) );
		SetMaterialDefaults( material, 0f );
		return material;
	}

	private Material CreateGpbrMaterial( string name, ExtractedPbrProperties props )
	{
		var material = Material.Create( name, "shaders/gmod_pbr.shader" );
		material.Set( "g_tColor", LoadTexture( props.BaseTexturePath, forceOpaqueAlpha: true ) ?? Texture.White );

		Texture roughnessTexture = DefaultRoughness;
		if ( !string.IsNullOrEmpty( props.MraoTexturePath ) )
		{
			var mraoData = LoadTextureRaw( props.MraoTexturePath );
			if ( mraoData != null )
			{
				var vtf = VtfFile.Load( mraoData );
				var rgba = vtf.ConvertToRGBA();
				if ( rgba != null )
					roughnessTexture = CreateTexture( PbrTextureGenerator.ExtractRoughnessFromMrao( rgba, vtf.Width, vtf.Height ), vtf.Width, vtf.Height );
			}
		}

		material.Set( "g_tRoughness", roughnessTexture );
		material.Set( "g_tNormal", LoadNormalMap( props.BumpMapPath ) );
		SetMaterialDefaults( material, 0f );
		return material;
	}

	private Material CreateMwbMaterial( string name, ExtractedPbrProperties props )
	{
		var material = Material.Create( name, "shaders/gmod_pbr.shader" );
		
		// MWB PBR: metalness is in base texture alpha
		Texture colorTexture = Texture.White;
		Texture metallicTexture = null;
		
		if ( !string.IsNullOrEmpty( props.BaseTexturePath ) )
		{
			var baseData = LoadTextureRaw( props.BaseTexturePath );
			Log.Info( $"    MWB base texture '{props.BaseTexturePath}': {(baseData != null ? $"{baseData.Length} bytes" : "null")}" );
			
			if ( baseData != null )
			{
				try
				{
					var vtf = VtfFile.Load( baseData );
					var rgba = vtf.ConvertToRGBA( forceOpaqueAlpha: false );
					Log.Info( $"    MWB base VTF: {vtf.Width}x{vtf.Height}, rgba={rgba != null}" );
					
					if ( rgba != null )
					{
						int pixels = vtf.Width * vtf.Height;
						
						// Extract metallic texture from alpha channel
						var metalData = new byte[pixels * 4];
						long metallicSum = 0;
						for ( int i = 0; i < pixels; i++ )
						{
							byte metal = rgba[i * 4 + 3]; // Alpha = metallic
							metallicSum += metal;
							metalData[i * 4 + 0] = metal;
							metalData[i * 4 + 1] = metal;
							metalData[i * 4 + 2] = metal;
							metalData[i * 4 + 3] = 255;
						}
						metallicTexture = CreateTexture( metalData, vtf.Width, vtf.Height );
						float avgMetallic = (metallicSum / (float)pixels) / 255f;
						Log.Info( $"    MWB metallic texture: {vtf.Width}x{vtf.Height}, avg={avgMetallic:F2}" );
						
						// Make color texture opaque
						for ( int i = 3; i < rgba.Length; i += 4 )
							rgba[i] = 255;
						colorTexture = CreateTexture( rgba, vtf.Width, vtf.Height );
					}
				}
				catch ( Exception ex )
				{
					Log.Warning( $"    MWB base texture error: {ex.Message}" );
				}
			}
		}
		
		material.Set( "g_tColor", colorTexture );
		material.Set( "g_tMetalness", metallicTexture ?? Texture.Black );
		material.Set( "g_flMetalnessScale", 1f );
		
		// MWB PBR: gloss is in normal map alpha - convert to roughness
		Texture roughnessTexture = DefaultRoughness;
		Texture normalTexture = DefaultNormal;
		
		Log.Info( $"    MWB bump path: '{props.BumpMapPath}'" );
		
		if ( !string.IsNullOrEmpty( props.BumpMapPath ) && !props.BumpMapPath.Contains( "null", StringComparison.OrdinalIgnoreCase ) )
		{
			var normalData = LoadTextureRaw( props.BumpMapPath );
			Log.Info( $"    MWB normal texture: {(normalData != null ? $"{normalData.Length} bytes" : "null")}" );
			
			if ( normalData != null )
			{
				try
				{
					var vtf = VtfFile.Load( normalData );
					var rgba = vtf.ConvertToRGBA();
					Log.Info( $"    MWB normal VTF: {vtf.Width}x{vtf.Height}, rgba={rgba != null}" );
					
					if ( rgba != null )
					{
						// MWB encoding: roughness -> invert to gloss -> pow(gloss, 2.5) -> sRGB to Linear
						// To decode: Linear to sRGB -> pow(x, 0.4) -> gloss -> invert to roughness
						var roughData = new byte[vtf.Width * vtf.Height * 4];
						long roughSum = 0;
						int pixels = vtf.Width * vtf.Height;
						
						for ( int i = 0; i < pixels; i++ )
						{
							byte encoded = rgba[i * 4 + 3]; // Alpha = linear encoded gloss
							float linear = encoded / 255f;
							// Convert linear back to sRGB: pow(x, 1/2.2) ≈ pow(x, 0.4545)
							float srgb = MathF.Pow( linear, 0.4545f );
							// Reverse pow(gloss, 2.5): pow(x, 0.4)
							float gloss = MathF.Pow( srgb, 0.4f );
							float roughness = 1.0f - gloss;
							byte roughByte = (byte)Math.Clamp( roughness * 255f, 0f, 255f );
							roughSum += roughByte;
							roughData[i * 4 + 0] = roughByte;
							roughData[i * 4 + 1] = roughByte;
							roughData[i * 4 + 2] = roughByte;
							roughData[i * 4 + 3] = 255;
						}
						
						float avgRoughness = (roughSum / (float)pixels);
						Log.Info( $"    MWB normal alpha decoded: avg roughness={avgRoughness:F1}/255 (lower = shinier)" );
						
						roughnessTexture = CreateTexture( roughData, vtf.Width, vtf.Height );
						
						// Make normal map alpha opaque (green flip disabled for testing)
						for ( int i = 0; i < rgba.Length; i += 4 )
						{
							// rgba[i + 1] = (byte)(255 - rgba[i + 1]); // Flip green - DISABLED
							rgba[i + 3] = 255; // Opaque alpha
						}
						normalTexture = CreateTexture( rgba, vtf.Width, vtf.Height );
						Log.Info( $"    MWB roughness and normal textures created" );
					}
				}
				catch ( Exception ex )
				{
					Log.Warning( $"    MWB normal texture error: {ex.Message}" );
				}
			}
		}
		
		Log.Info( $"  MWB PBR: metallic texture from base alpha, roughness from normal alpha" );
		
		material.Set( "g_tRoughness", roughnessTexture );
		material.Set( "g_tNormal", normalTexture );
		material.Set( "g_flRoughnessScaleFactor", 1f );
		material.Set( "g_tAmbientOcclusion", DefaultAo );
		material.Set( "g_tTintMask", Texture.White );
		material.Set( "g_tSelfIllumMask", Texture.Black );
		material.Set( "g_flSelfIllumScale", 0f );
		return material;
	}

	private Material CreateBftMaterial( string name, ExtractedPbrProperties props )
	{
		var material = Material.Create( name, "shaders/gmod_pbr.shader" );
		
		// BFT: metallic is in base texture alpha (with $blendTintByBaseAlpha)
		Texture colorTexture = Texture.White;
		Texture metallicTexture = null;
		
		if ( !string.IsNullOrEmpty( props.BaseTexturePath ) )
		{
			var baseData = LoadTextureRaw( props.BaseTexturePath );
			if ( baseData != null )
			{
				try
				{
					var vtf = VtfFile.Load( baseData );
					var rgba = vtf.ConvertToRGBA( forceOpaqueAlpha: false );
					if ( rgba != null )
					{
						int pixels = vtf.Width * vtf.Height;
						
						// Check if alpha channel has meaningful metallic data
						// If alpha is mostly 255 (opaque), don't use it as metallic
						long alphaSum = 0;
						for ( int i = 0; i < pixels; i++ )
							alphaSum += rgba[i * 4 + 3];
						float avgAlpha = alphaSum / (float)pixels;
						
						// Only extract metallic if alpha varies (not all opaque)
						if ( avgAlpha < 250f )
						{
							var metalData = new byte[pixels * 4];
							for ( int i = 0; i < pixels; i++ )
							{
								byte metal = rgba[i * 4 + 3];
								metalData[i * 4 + 0] = metal;
								metalData[i * 4 + 1] = metal;
								metalData[i * 4 + 2] = metal;
								metalData[i * 4 + 3] = 255;
							}
							metallicTexture = CreateTexture( metalData, vtf.Width, vtf.Height );
							Log.Info( $"    BFT metallic from alpha: avgAlpha={avgAlpha:F1}" );
						}
						else
						{
							Log.Info( $"    BFT skipping metallic (alpha is opaque: {avgAlpha:F1})" );
						}
						
						// Make color texture opaque
						for ( int i = 3; i < rgba.Length; i += 4 )
							rgba[i] = 255;
						colorTexture = CreateTexture( rgba, vtf.Width, vtf.Height );
					}
				}
				catch { }
			}
		}
		
		material.Set( "g_tColor", colorTexture );
		material.Set( "g_tMetalness", metallicTexture ?? Texture.Black );
		material.Set( "g_flMetalnessScale", metallicTexture != null ? 1f : 0f );

		// BFT: roughness from exponent red channel with pow(0.28) decode
		Texture roughnessTexture = DefaultRoughness;
		if ( !string.IsNullOrEmpty( props.PhongExponentTexturePath ) )
		{
			var expData = LoadTextureRaw( props.PhongExponentTexturePath );
			if ( expData != null )
			{
				try
				{
					var vtf = VtfFile.Load( expData );
					var rgba = vtf.ConvertToRGBA();
					if ( rgba != null )
						roughnessTexture = CreateTexture( PbrTextureGenerator.ConvertBftExponentToRoughness( rgba, vtf.Width, vtf.Height ), vtf.Width, vtf.Height );
				}
				catch { }
			}
		}

		material.Set( "g_tRoughness", roughnessTexture );
		material.Set( "g_tNormal", LoadNormalMap( props.BumpMapPath ) );
		material.Set( "g_tAmbientOcclusion", DefaultAo );
		material.Set( "g_tTintMask", Texture.White );
		material.Set( "g_tSelfIllumMask", Texture.Black );
		material.Set( "g_flSelfIllumScale", 0f );
		material.Set( "g_flRoughnessScaleFactor", 1f );
		return material;
	}

	/// <summary>
	/// Create material for MadIvan18 models.
	/// Roughness: normal map alpha
	/// Metalness: exponent map red channel
	/// </summary>
	private Material CreateMadIvan18Material( string name, ExtractedPbrProperties props )
	{
		// Choose shader based on transparency and culling needs
		bool needsTranslucency = props.IsTranslucent || props.IsAdditive;
		string shaderPath = GetShaderPath( needsTranslucency, props.IsNoCull );
		var material = Material.Create( name, shaderPath );
		
		// Load base color texture
		material.Set( "g_tColor", LoadTexture( props.BaseTexturePath ) ?? Texture.White );
		
		// Roughness from normal map alpha channel
		Texture roughnessTexture = DefaultRoughness;
		Texture normalTexture = DefaultNormal;
		
		if ( !string.IsNullOrEmpty( props.BumpMapPath ) )
		{
			var normalData = LoadTextureRaw( props.BumpMapPath );
			if ( normalData != null )
			{
				try
				{
					var vtf = VtfFile.Load( normalData );
					var rgba = vtf.ConvertToRGBA( forceOpaqueAlpha: false );
					if ( rgba != null )
					{
						int pixels = vtf.Width * vtf.Height;
						
						// Extract roughness from alpha channel
						// MadIvan18 stores GLOSS in alpha: black=matte, white=shiny
						// PBR roughness is opposite: black=shiny, white=matte
						// So we invert: roughness = 255 - gloss
						var roughData = new byte[pixels * 4];
						for ( int i = 0; i < pixels; i++ )
						{
							byte gloss = rgba[i * 4 + 3];
							byte roughness = (byte)(255 - gloss); // Invert gloss to roughness
							roughData[i * 4 + 0] = roughness;
							roughData[i * 4 + 1] = roughness;
							roughData[i * 4 + 2] = roughness;
							roughData[i * 4 + 3] = 255;
						}
						roughnessTexture = CreateTexture( roughData, vtf.Width, vtf.Height );
						Log.Info( $"    MadIvan18: roughness from normal map alpha (inverted from gloss)" );
						
						// Flip green channel for normal map (DirectX to OpenGL)
						// DISABLED FOR TESTING
						for ( int i = 0; i < pixels; i++ )
						{
							// rgba[i * 4 + 1] = (byte)(255 - rgba[i * 4 + 1]); // Flip green - DISABLED
							rgba[i * 4 + 3] = 255; // Make normal map opaque
						}
						normalTexture = CreateTexture( rgba, vtf.Width, vtf.Height );
					}
				}
				catch { }
			}
		}
		
		// Metalness from exponent map red channel
		Texture metalnessTexture = Texture.Black;
		float metalnessScale = 0f;
		
		if ( !string.IsNullOrEmpty( props.PhongExponentTexturePath ) )
		{
			var expData = LoadTextureRaw( props.PhongExponentTexturePath );
			if ( expData != null )
			{
				try
				{
					var vtf = VtfFile.Load( expData );
					var rgba = vtf.ConvertToRGBA();
					if ( rgba != null )
					{
						int pixels = vtf.Width * vtf.Height;
						
						// Red channel contains metalness
						var metalData = new byte[pixels * 4];
						for ( int i = 0; i < pixels; i++ )
						{
							byte metal = rgba[i * 4 + 0]; // Red channel
							metalData[i * 4 + 0] = metal;
							metalData[i * 4 + 1] = metal;
							metalData[i * 4 + 2] = metal;
							metalData[i * 4 + 3] = 255;
						}
						metalnessTexture = CreateTexture( metalData, vtf.Width, vtf.Height );
						metalnessScale = 1f;
						Log.Info( $"    MadIvan18: metalness from exponent red channel" );
					}
				}
				catch { }
			}
		}
		
		material.Set( "g_tNormal", normalTexture );
		material.Set( "g_tRoughness", roughnessTexture );
		material.Set( "g_tMetalness", metalnessTexture );
		material.Set( "g_flMetalnessScale", metalnessScale );
		material.Set( "g_flRoughnessScaleFactor", 1f );
		material.Set( "g_tAmbientOcclusion", DefaultAo );
		
		// Alpha test (only for opaque shader)
		if ( props.IsAlphaTest && !needsTranslucency )
			material.Set( "g_flAlphaTestReference", props.AlphaTestReference );
		
		// Translucent or additive - set opacity multiplier
		if ( needsTranslucency )
			material.Set( "g_flOpacity", props.Alpha );
		
		// Apply color tinting
		ApplyColorTinting( material, props );
		
		return material;
	}

	private Material CreateSourceMaterial( string name, ExtractedPbrProperties props )
	{
		// Choose shader based on transparency and culling needs
		bool needsTranslucency = props.IsTranslucent || props.IsAdditive;
		string shaderPath = GetShaderPath( needsTranslucency, props.IsNoCull );
		var material = Material.Create( name, shaderPath );
		
		// Preserve alpha for alpha test, translucent, and additive materials
		bool needsAlpha = props.IsAlphaTest || needsTranslucency;
		if ( props.IsTranslucent )
			Log.Info( $"    Translucent material (using translucent shader)" );
		if ( props.IsAlphaTest )
			Log.Info( $"    AlphaTest material: ref={props.AlphaTestReference}" );
		if ( props.IsAdditive )
			Log.Info( $"    Additive material (using translucent shader)" );
		if ( props.IsNoCull )
			Log.Info( $"    NoCull material (using twosided shader)" );
		
		var colorTexture = LoadTexture( props.BaseTexturePath, forceOpaqueAlpha: !needsAlpha );
		// g_tColor - main iris/sclera texture
		Texture irisTex = null;
		if ( !string.IsNullOrEmpty( props.IrisTexturePath ) )
		{
			irisTex = LoadTexture( props.IrisTexturePath );
			Log.Info( $"    Iris texture loaded: {irisTex != null}" );
		}
		material.Set( "g_tColor", irisTex ?? Texture.White );
		
		// g_tNormal - cornea/sclera normal map
		if ( !string.IsNullOrEmpty( props.CorneaTexturePath ) )
			material.Set( "g_tNormal", LoadTexture( props.CorneaTexturePath ) ?? DefaultNormal );
		else
			material.Set( "g_tNormal", DefaultNormal );
		
		// g_tIrisNormal - iris-specific normal
		material.Set( "g_tIrisNormal", DefaultNormal );
		
		// g_tOcclusion - ambient occlusion
		if ( !string.IsNullOrEmpty( props.EyeAmbientOcclTexturePath ) )
			material.Set( "g_tOcclusion", LoadTexture( props.EyeAmbientOcclTexturePath ) ?? Texture.White );
		else
			material.Set( "g_tOcclusion", Texture.White );
		
		// g_tIrisRoughness
		material.Set( "g_tIrisRoughness", DefaultRoughness );
		
		// g_tIrisMask - height/parallax mask
		material.Set( "g_tIrisMask", Texture.White );
		
		// g_tReflectance
		material.Set( "g_tReflectance", Texture.White );
		
		// Alpha test (only for opaque shader)
		if ( props.IsAlphaTest && !needsTranslucency )
			material.Set( "g_flAlphaTestReference", props.AlphaTestReference );
		
		// Translucent or additive - set opacity multiplier
		if ( needsTranslucency )
			material.Set( "g_flOpacity", props.Alpha );
		
		// Apply color tinting
		ApplyColorTinting( material, props );
		
		return material;
	}

	private static string GetShaderPath( bool translucent, bool noCull )
	{
		if ( translucent && noCull )
			return "shaders/gmod_pbr_translucent_twosided.shader";
		if ( translucent )
			return "shaders/gmod_pbr_translucent.shader";
		if ( noCull )
			return "shaders/gmod_pbr_twosided.shader";
		return "shaders/gmod_pbr.shader";
	}
	
	/// <summary>
	/// Create material for Source Engine eye shaders (Eyes/EyeRefract).
	/// Uses our custom gmod_eyes.shader with Source-style projection.
	/// </summary>
	private Material CreateEyeMaterial( string name, ExtractedPbrProperties props )
	{
		var material = Material.Create( name, "eyeball.shader" );
		
		// Debug: log what textures we're loading
		Log.Info( $"    Eye iris: '{props.IrisTexturePath}'" );
		Log.Info( $"    Eye cornea: '{props.CorneaTexturePath}'" );
		
		// g_tIris - main iris/sclera texture
		Texture irisTex = null;
		if ( !string.IsNullOrEmpty( props.IrisTexturePath ) )
		{
			irisTex = LoadTexture( props.IrisTexturePath );
			Log.Info( $"    Iris texture loaded: {irisTex != null}" );
		}
		material.Set( "g_tColor", irisTex ?? Texture.White );
		
		if ( !string.IsNullOrEmpty( props.CorneaTexturePath ) )
			material.Set( "g_tNormal", LoadTexture( props.CorneaTexturePath ) ?? DefaultNormal );
		else
			material.Set( "g_tNormal", DefaultNormal );
		
		// g_tIrisNormal - iris-specific normal
		material.Set( "g_tIrisNormal", DefaultNormal );
		
		// g_tOcclusion - ambient occlusion
		if ( !string.IsNullOrEmpty( props.EyeAmbientOcclTexturePath ) )
			material.Set( "g_tOcclusion", LoadTexture( props.EyeAmbientOcclTexturePath ) ?? Texture.White );
		else
			material.Set( "g_tOcclusion", Texture.White );
		
		// g_tIrisRoughness
		material.Set( "g_tIrisRoughness", DefaultRoughness );
		
		// g_tIrisMask - height/parallax mask
		material.Set( "g_tIrisMask", Texture.White );
		
		// g_tReflectance
		material.Set( "g_tReflectance", Texture.White );
		
		return material;
	}
	
	/// <summary>
	/// Apply $color2 tinting to material if specified in properties.
	/// </summary>
	private static void ApplyColorTinting( Material material, ExtractedPbrProperties props )
	{
		if ( props.Color2 != null && props.Color2.Length >= 3 )
		{
			material.Set( "g_vColorTint", new Vector3( props.Color2[0], props.Color2[1], props.Color2[2] ) );
			
			// For opaque shaders, enable blend-by-alpha mode if specified
			if ( props.BlendTintByBaseAlpha )
				material.Set( "g_flBlendTintByBaseAlpha", 1f );
		}
	}

	private void SetMaterialDefaults( Material material, float metalness )
	{
		material.Set( "g_tMetalness", Texture.Black );
		material.Set( "g_flMetalnessScale", metalness );
		material.Set( "g_flRoughnessScaleFactor", 1f );
		material.Set( "g_tAmbientOcclusion", DefaultAo );
	}

	private Texture LoadNormalMap( string path, bool flipGreen = true )
	{
		if ( string.IsNullOrEmpty( path ) || path.Contains( "null", StringComparison.OrdinalIgnoreCase ) )
			return DefaultNormal;

		var data = LoadTextureRaw( path );
		if ( data == null )
			return DefaultNormal;

		try
		{
			var vtf = VtfFile.Load( data );
			var rgba = vtf.ConvertToRGBA();
			if ( rgba == null )
				return DefaultNormal;

			// Flip green channel: Source uses DirectX convention, s&box uses OpenGL
			// DISABLED FOR TESTING
			// if ( flipGreen )
			// {
			//	for ( int i = 0; i < rgba.Length; i += 4 )
			//		rgba[i + 1] = (byte)(255 - rgba[i + 1]);
			// }

			return CreateTexture( rgba, vtf.Width, vtf.Height );
		}
		catch
		{
			return DefaultNormal;
		}
	}

	private byte[] LoadTextureRaw( string textureName )
	{
		if ( string.IsNullOrEmpty( textureName ) )
			return null;

		var vtfRelPath = "materials/" + textureName.ToLowerInvariant().Replace( '\\', '/' );
		if ( !vtfRelPath.EndsWith( ".vtf" ) )
			vtfRelPath += ".vtf";

		// Check loose files first
		var loosePath = System.IO.Path.Combine( _rootPath, vtfRelPath.Replace( '/', '\\' ) );
		
		if ( File.Exists( loosePath ) )
			return File.ReadAllBytes( loosePath );
		
		return _mount.ReadFile( vtfRelPath );
	}

	private Texture LoadTexture( string texturePath, bool forceOpaqueAlpha = false )
	{
		var data = LoadTextureRaw( texturePath );
		if ( data == null )
			return null;

		try
		{
			var vtf = VtfFile.Load( data );
			var rgbaData = vtf.ConvertToRGBA( forceOpaqueAlpha );
			if ( rgbaData == null )
				return null;

			return CreateTexture( rgbaData, vtf.Width, vtf.Height );
		}
		catch
		{
			return null;
		}
	}

	private Texture LoadTextureWithAlphaDebug( string texturePath, bool forceOpaqueAlpha = false, bool logAlpha = false )
	{
		var data = LoadTextureRaw( texturePath );
		if ( data == null )
			return null;

		try
		{
			var vtf = VtfFile.Load( data );
			var rgbaData = vtf.ConvertToRGBA( forceOpaqueAlpha );
			if ( rgbaData == null )
				return null;

			if ( logAlpha )
			{
				// Calculate average alpha value
				float totalAlpha = 0;
				int minAlpha = 255, maxAlpha = 0;
				int pixelCount = vtf.Width * vtf.Height;
				for ( int i = 0; i < pixelCount; i++ )
				{
					int alpha = rgbaData[i * 4 + 3];
					totalAlpha += alpha;
					if ( alpha < minAlpha ) minAlpha = alpha;
					if ( alpha > maxAlpha ) maxAlpha = alpha;
				}
				float avgAlpha = totalAlpha / pixelCount;
				Log.Info( $"    Texture '{texturePath}' alpha: avg={avgAlpha:F1}, min={minAlpha}, max={maxAlpha}" );
			}

			return CreateTexture( rgbaData, vtf.Width, vtf.Height );
		}
		catch
		{
			return null;
		}
	}

	private static Texture CreateTexture( byte[] rgba, int width, int height )
	{
		return Texture.Create( width, height )
			.WithData( rgba )
			.WithMips()
			.Finish();
	}
}

/// <summary>
/// Resource loader for loose sound files (.wav, .mp3)
/// </summary>
internal class GModSoundLoose : ResourceLoader<GModMount>
{
	private readonly string _filePath;

	public GModSoundLoose( string filePath )
	{
		_filePath = filePath;
	}

	protected override object Load()
	{
		try
		{
			var data = File.ReadAllBytes( _filePath );
			// Return raw sound data - s&box handles WAV/MP3 natively
			return data;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"GModSoundLoose: Failed to load {_filePath}: {ex.Message}" );
			return null;
		}
	}
}
