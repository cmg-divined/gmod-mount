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
			var sourceModel = SourceModel.Load( mdlData, vvdData, vtxData, phyData );
			return SourceModelLoader.Convert( sourceModel, path: Path, mount: _mount );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"GModModelLoose: Failed to load {_filePath}: {ex.Message}" );
			return Model.Error;
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

			// Detect format and extract PBR properties
			var pbrProps = PseudoPbrFormats.ExtractProperties( vmt );
			
			// Log detected format
			Log.Info( $"GModMaterialLoose: {_filePath} -> {pbrProps.Format} (shader: {vmt.Shader})" );

			// Create material based on detected format
			var relativePath = System.IO.Path.GetRelativePath( _rootPath, _filePath ).Replace( '\\', '/' );
			var materialName = relativePath.Replace( ".vmt", "" );
			
			return pbrProps.Format switch
			{
				PbrFormat.ExoPBR => CreateExoPbrMaterial( materialName, pbrProps ),
				PbrFormat.GPBR => CreateGpbrMaterial( materialName, pbrProps ),
				PbrFormat.MWBPBR => CreateMwbMaterial( materialName, pbrProps ),
				PbrFormat.BFTPseudoPBR => CreateBftMaterial( materialName, pbrProps ),
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
		var material = Material.Create( name, "complex" );
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
		material.Set( "g_tNormal", LoadNormalMap( props.ExoNormalPath ?? props.BumpMapPath, flipGreen: !string.IsNullOrEmpty( props.ExoNormalPath ) ) );
		SetMaterialDefaults( material, 0f );
		return material;
	}

	private Material CreateGpbrMaterial( string name, ExtractedPbrProperties props )
	{
		var material = Material.Create( name, "complex" );
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
						Log.Info( $"    MWB metallic texture created, avg={avgMetallic:F2}" );
						
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
		
		// Set metallic texture
		if ( metallicTexture != null )
		{
			material.Set( "g_tMetalness", metallicTexture );
		}
		
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
						
						// Make normal map alpha opaque
						for ( int i = 3; i < rgba.Length; i += 4 )
							rgba[i] = 255;
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
		var material = Material.Create( name, "complex" );
		material.Set( "g_tColor", LoadTexture( props.BaseTexturePath, forceOpaqueAlpha: true ) ?? Texture.White );

		Texture roughnessTexture = DefaultRoughness;
		if ( !string.IsNullOrEmpty( props.PhongExponentTexturePath ) )
		{
			var expData = LoadTextureRaw( props.PhongExponentTexturePath );
			if ( expData != null )
			{
				var vtf = VtfFile.Load( expData );
				var rgba = vtf.ConvertToRGBA();
				if ( rgba != null )
					roughnessTexture = CreateTexture( PbrTextureGenerator.ConvertBftExponentToRoughness( rgba, vtf.Width, vtf.Height ), vtf.Width, vtf.Height );
			}
		}

		material.Set( "g_tRoughness", roughnessTexture );
		material.Set( "g_tNormal", LoadNormalMap( props.BumpMapPath ) );
		SetMaterialDefaults( material, props.IsBftMetallicLayer ? 0.9f : 0f );
		return material;
	}

	private Material CreateSourceMaterial( string name, ExtractedPbrProperties props )
	{
		var material = Material.Create( name, "complex" );
		material.Set( "g_tColor", LoadTexture( props.BaseTexturePath, forceOpaqueAlpha: !props.IsTranslucent ) ?? Texture.White );
		material.Set( "g_tNormal", LoadNormalMap( props.BumpMapPath ) );
		material.Set( "g_tRoughness", DefaultRoughness );
		material.Set( "g_flMetalness", props.Metallic );
		material.Set( "g_flRoughnessScaleFactor", props.Roughness );
		material.Set( "g_tAmbientOcclusion", DefaultAo );
		material.Set( "g_tTintMask", Texture.White );
		material.Set( "g_tSelfIllumMask", props.IsSelfIllum ? Texture.White : Texture.Black );
		material.Set( "g_flSelfIllumScale", props.IsSelfIllum ? 1f : 0f );
		return material;
	}

	private void SetMaterialDefaults( Material material, float metalness )
	{
		material.Set( "g_flMetalness", metalness );
		material.Set( "g_flRoughnessScaleFactor", 1f );
		material.Set( "g_tAmbientOcclusion", DefaultAo );
		material.Set( "g_tTintMask", Texture.White );
		material.Set( "g_tSelfIllumMask", Texture.Black );
		material.Set( "g_flSelfIllumScale", 0f );
	}

	private Texture LoadNormalMap( string path, bool flipGreen = false )
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

			if ( flipGreen )
				rgba = PbrTextureGenerator.FlipNormalMapGreen( rgba, vtf.Width, vtf.Height );

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
