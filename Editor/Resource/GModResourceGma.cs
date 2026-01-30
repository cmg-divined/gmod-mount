using GModMount.Source;

namespace GModMount;

/// <summary>
/// Resource loader for GMA-packaged models (.mdl)
/// </summary>
internal class GModModelGma : ResourceLoader<GModMount>
{
	private readonly GModMount _mount;
	private readonly GmaArchive _archive;
	private readonly GmaEntry _entry;

	public GModModelGma( GModMount mount, GmaArchive archive, GmaEntry entry )
	{
		_mount = mount;
		_archive = archive;
		_entry = entry;
	}

	protected override object Load()
	{
		var basePath = _entry.Path[..^4];
		
		try
		{
			Log.Info( $"GModModelGma: Loading {_entry.Path}" );
			
			// Read MDL data from GMA
			var mdlData = _archive.ReadFile( _entry );
			if ( mdlData == null || mdlData.Length < 408 )
			{
				Log.Warning( $"  MDL data missing or too small: {mdlData?.Length ?? 0} bytes" );
				return Model.Error;
			}
			Log.Info( $"  MDL: {mdlData.Length} bytes" );

			// Read VVD (check GMA first, then fall back to mount)
			var vvdPath = basePath + ".vvd";
			var vvdData = _archive.ReadFile( vvdPath ) ?? _mount.ReadFile( vvdPath );
			if ( vvdData == null )
			{
				Log.Warning( $"  VVD missing: {vvdPath}" );
				return Model.Error;
			}
			Log.Info( $"  VVD: {vvdData.Length} bytes" );

			// Read VTX
			string vtxPath = basePath + ".dx90.vtx";
			var vtxData = _archive.ReadFile( vtxPath ) ?? _mount.ReadFile( vtxPath );
			if ( vtxData == null )
			{
				vtxPath = basePath + ".dx80.vtx";
				vtxData = _archive.ReadFile( vtxPath ) ?? _mount.ReadFile( vtxPath );
			}
			if ( vtxData == null )
			{
				vtxPath = basePath + ".sw.vtx";
				vtxData = _archive.ReadFile( vtxPath ) ?? _mount.ReadFile( vtxPath );
			}
			if ( vtxData == null )
			{
				Log.Warning( $"  VTX missing for {basePath}" );
				return Model.Error;
			}
			Log.Info( $"  VTX: {vtxData.Length} bytes ({vtxPath})" );

			// Read PHY (optional)
			var phyPath = basePath + ".phy";
			var phyData = _archive.ReadFile( phyPath ) ?? _mount.ReadFile( phyPath );
			Log.Info( $"  PHY: {(phyData != null ? $"{phyData.Length} bytes" : "none")}" );

			// Parse and convert
			Log.Info( $"  Parsing SourceModel..." );
			var sourceModel = SourceModel.Load( mdlData, vvdData, vtxData, phyData );
			Log.Info( $"  MDL v{sourceModel.Mdl.Version}, {sourceModel.Mdl.Bones?.Count ?? 0} bones, {sourceModel.Mdl.BodyParts?.Count ?? 0} bodyparts, {sourceModel.Mdl.Materials?.Count ?? 0} materials" );
			
			Log.Info( $"  Converting to s&box model..." );
			var result = SourceModelLoader.Convert( sourceModel, path: Path, mount: _mount );
			Log.Info( $"  Success: {_entry.Path}" );
			return result;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"GModModelGma: Failed to load {_entry.Path}: {ex.Message}" );
			Log.Warning( $"  Stack: {ex.StackTrace}" );
			return Model.Error;
		}
	}
}

/// <summary>
/// Resource loader for GMA-packaged textures (.vtf)
/// </summary>
internal class GModTextureGma : ResourceLoader<GModMount>
{
	private readonly GModMount _mount;
	private readonly GmaArchive _archive;
	private readonly GmaEntry _entry;

	public GModTextureGma( GModMount mount, GmaArchive archive, GmaEntry entry )
	{
		_mount = mount;
		_archive = archive;
		_entry = entry;
	}

	protected override object Load()
	{
		try
		{
			var data = _archive.ReadFile( _entry );
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
			Log.Warning( $"GModTextureGma: Failed to load {_entry.Path}: {ex.Message}" );
			return Texture.Invalid;
		}
	}
}

/// <summary>
/// Resource loader for GMA-packaged materials (.vmt) with pseudo-PBR support.
/// </summary>
internal class GModMaterialGma : ResourceLoader<GModMount>
{
	private readonly GModMount _mount;
	private readonly GmaArchive _archive;
	private readonly GmaEntry _entry;

	public GModMaterialGma( GModMount mount, GmaArchive archive, GmaEntry entry )
	{
		_mount = mount;
		_archive = archive;
		_entry = entry;
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
			var data = _archive.ReadFile( _entry );
			if ( data == null )
				return null;

			string vmtContent = System.Text.Encoding.UTF8.GetString( data );
			var vmt = VmtFile.Load( vmtContent );

			// Detect format and extract PBR properties
			var pbrProps = PseudoPbrFormats.ExtractProperties( vmt );
			
			// Log detected format
			Log.Info( $"GModMaterialGma: {_entry.Path} -> {pbrProps.Format} (shader: {vmt.Shader})" );

			// Create material based on detected format
			var materialName = _entry.Path.Replace( ".vmt", "" ).Replace( '\\', '/' );
			
			return pbrProps.Format switch
			{
				PbrFormat.ExoPBR => CreateExoPbrMaterial( materialName, pbrProps, vmt ),
				PbrFormat.GPBR => CreateGpbrMaterial( materialName, pbrProps, vmt ),
				PbrFormat.MWBPBR => CreateMwbMaterial( materialName, pbrProps, vmt ),
				PbrFormat.BFTPseudoPBR => CreateBftMaterial( materialName, pbrProps, vmt ),
				_ => CreateSourceMaterial( materialName, pbrProps, vmt )
			};
		}
		catch ( Exception ex )
		{
			Log.Warning( $"GModMaterialGma: Failed to load {_entry.Path}: {ex.Message}" );
			return null;
		}
	}

	private Material CreateExoPbrMaterial( string name, ExtractedPbrProperties props, VmtFile vmt )
	{
		var material = Material.Create( name, "shaders/gmod_pbr.shader" );
		var colorTexture = LoadTexture( props.BaseTexturePath, forceOpaqueAlpha: true ) ?? Texture.White;
		material.Set( "g_tColor", colorTexture );

		// Process ARM texture
		Texture roughnessTexture = DefaultRoughness;
		if ( !string.IsNullOrEmpty( props.ArmTexturePath ) )
		{
			var armData = LoadTextureRaw( props.ArmTexturePath );
			if ( armData != null )
			{
				var vtf = VtfFile.Load( armData );
				var rgba = vtf.ConvertToRGBA();
				if ( rgba != null )
				{
					var roughData = PbrTextureGenerator.ExtractRoughnessFromArm( rgba, vtf.Width, vtf.Height );
					roughnessTexture = CreateTexture( roughData, vtf.Width, vtf.Height );
				}
			}
		}

		material.Set( "g_tRoughness", roughnessTexture );
		material.Set( "g_tNormal", LoadNormalMap( props.ExoNormalPath ?? props.BumpMapPath, props.IsSSBump ) );
		material.Set( "g_tMetalness", Texture.Black );
		material.Set( "g_flMetalnessScale", 0f );
		material.Set( "g_tAmbientOcclusion", DefaultAo );
		material.Set( "g_flRoughnessScaleFactor", 1f );
		return material;
	}

	private Material CreateGpbrMaterial( string name, ExtractedPbrProperties props, VmtFile vmt )
	{
		var material = Material.Create( name, "shaders/gmod_pbr.shader" );
		var colorTexture = LoadTexture( props.BaseTexturePath, forceOpaqueAlpha: true ) ?? Texture.White;
		material.Set( "g_tColor", colorTexture );

		Texture roughnessTexture = DefaultRoughness;
		if ( !string.IsNullOrEmpty( props.MraoTexturePath ) )
		{
			var mraoData = LoadTextureRaw( props.MraoTexturePath );
			if ( mraoData != null )
			{
				var vtf = VtfFile.Load( mraoData );
				var rgba = vtf.ConvertToRGBA();
				if ( rgba != null )
				{
					var roughData = PbrTextureGenerator.ExtractRoughnessFromMrao( rgba, vtf.Width, vtf.Height );
					roughnessTexture = CreateTexture( roughData, vtf.Width, vtf.Height );
				}
			}
		}

		material.Set( "g_tRoughness", roughnessTexture );
		material.Set( "g_tNormal", LoadNormalMap( props.BumpMapPath, props.IsSSBump ) );
		material.Set( "g_tMetalness", Texture.Black );
		material.Set( "g_flMetalnessScale", 0f );
		material.Set( "g_tAmbientOcclusion", DefaultAo );
		material.Set( "g_flRoughnessScaleFactor", 1f );
		return material;
	}

	private Material CreateMwbMaterial( string name, ExtractedPbrProperties props, VmtFile vmt )
	{
		var material = Material.Create( name, "shaders/gmod_pbr.shader" );
		
		// MWB PBR: metalness is in base texture alpha
		Texture colorTexture = Texture.White;
		Texture metallicTexture = null;
		
		if ( !string.IsNullOrEmpty( props.BaseTexturePath ) )
		{
			var baseData = LoadTextureRaw( props.BaseTexturePath );
			if ( baseData != null )
			{
				var vtf = VtfFile.Load( baseData );
				var rgba = vtf.ConvertToRGBA( forceOpaqueAlpha: false );
				if ( rgba != null )
				{
					int pixels = vtf.Width * vtf.Height;
					
					// Extract metallic texture from alpha channel
					var metalData = new byte[pixels * 4];
					for ( int i = 0; i < pixels; i++ )
					{
						byte metal = rgba[i * 4 + 3]; // Alpha = metallic
						metalData[i * 4 + 0] = metal;
						metalData[i * 4 + 1] = metal;
						metalData[i * 4 + 2] = metal;
						metalData[i * 4 + 3] = 255;
					}
					metallicTexture = CreateTexture( metalData, vtf.Width, vtf.Height );
					
					// Make color texture opaque
					for ( int i = 3; i < rgba.Length; i += 4 )
						rgba[i] = 255;
					colorTexture = CreateTexture( rgba, vtf.Width, vtf.Height );
				}
			}
		}
		
		material.Set( "g_tColor", colorTexture );
		material.Set( "g_tMetalness", metallicTexture ?? Texture.Black );
		material.Set( "g_flMetalnessScale", 1f );
		
		// MWB PBR: gloss is in normal map alpha - convert to roughness
		Texture roughnessTexture = DefaultRoughness;
		Texture normalTexture = DefaultNormal;
		
		if ( !string.IsNullOrEmpty( props.BumpMapPath ) && !props.BumpMapPath.Contains( "null", StringComparison.OrdinalIgnoreCase ) )
		{
			var normalData = LoadTextureRaw( props.BumpMapPath );
			if ( normalData != null )
			{
				var vtf = VtfFile.Load( normalData );
				var rgba = vtf.ConvertToRGBA();
				if ( rgba != null )
				{
					// MWB encoding: roughness -> invert to gloss -> pow(gloss, 2.5) -> sRGB to Linear
					// To decode: Linear to sRGB -> pow(x, 0.4) -> gloss -> invert to roughness
					var roughData = new byte[vtf.Width * vtf.Height * 4];
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
						roughData[i * 4 + 0] = roughByte;
						roughData[i * 4 + 1] = roughByte;
						roughData[i * 4 + 2] = roughByte;
						roughData[i * 4 + 3] = 255;
					}
					roughnessTexture = CreateTexture( roughData, vtf.Width, vtf.Height );
					
					// Make normal map alpha opaque and flip green channel (DirectX to OpenGL)
					for ( int i = 0; i < rgba.Length; i += 4 )
					{
						rgba[i + 1] = (byte)(255 - rgba[i + 1]); // Flip green
						rgba[i + 3] = 255; // Opaque alpha
					}
					normalTexture = CreateTexture( rgba, vtf.Width, vtf.Height );
				}
			}
		}
		
		Log.Info( $"  MWB PBR: metallic texture from base alpha, roughness from normal alpha" );
		
		material.Set( "g_tRoughness", roughnessTexture );
		material.Set( "g_tNormal", normalTexture );
		material.Set( "g_tAmbientOcclusion", DefaultAo );
		material.Set( "g_tTintMask", Texture.White );
		material.Set( "g_tSelfIllumMask", Texture.Black );
		material.Set( "g_flSelfIllumScale", 0f );
		material.Set( "g_flRoughnessScaleFactor", 1f );
		return material;
	}

	private Material CreateBftMaterial( string name, ExtractedPbrProperties props, VmtFile vmt )
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
		material.Set( "g_tNormal", LoadNormalMap( props.BumpMapPath, props.IsSSBump ) );
		material.Set( "g_tAmbientOcclusion", DefaultAo );
		material.Set( "g_tTintMask", Texture.White );
		material.Set( "g_tSelfIllumMask", Texture.Black );
		material.Set( "g_flSelfIllumScale", 0f );
		material.Set( "g_flRoughnessScaleFactor", 1f );
		return material;
	}

	private Material CreateSourceMaterial( string name, ExtractedPbrProperties props, VmtFile vmt )
	{
		// Choose shader based on transparency and culling needs
		bool needsTranslucency = props.IsTranslucent || props.IsAdditive;
		string shaderPath = GetShaderPath( needsTranslucency, props.IsNoCull );
		var material = Material.Create( name, shaderPath );
		
		// Preserve alpha for alpha test, translucent, and additive materials
		bool needsAlpha = props.IsAlphaTest || needsTranslucency;
		var colorTexture = LoadTexture( props.BaseTexturePath, forceOpaqueAlpha: !needsAlpha ) ?? Texture.White;
		material.Set( "g_tColor", colorTexture );
		material.Set( "g_tNormal", LoadNormalMap( props.BumpMapPath, props.IsSSBump ) );
		material.Set( "g_tRoughness", DefaultRoughness );
		material.Set( "g_tMetalness", Texture.Black );
		material.Set( "g_flMetalnessScale", props.Metallic );
		material.Set( "g_flRoughnessScaleFactor", props.Roughness );
		material.Set( "g_tAmbientOcclusion", DefaultAo );
		
		// Alpha test (only for opaque shader)
		if ( props.IsAlphaTest && !needsTranslucency )
			material.Set( "g_flAlphaTestReference", props.AlphaTestReference );
		
		// Translucent or additive - opacity comes from texture alpha
		if ( props.IsTranslucent || props.IsAdditive )
		{
			material.Set( "g_flOpacity", props.Alpha );
		}
		
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

	private Texture LoadNormalMap( string path, bool isSSBump, bool flipGreen = true )
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
			if ( flipGreen )
			{
				for ( int i = 0; i < rgba.Length; i += 4 )
					rgba[i + 1] = (byte)(255 - rgba[i + 1]);
			}

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

		var normalizedPath = textureName.Replace( '\\', '/' ).ToLowerInvariant().Trim( '/' );
		var vtfPath = $"materials/{normalizedPath}.vtf";
		return _mount.ReadFile( vtfPath );
	}

	private Texture LoadTexture( string texturePath, bool forceOpaqueAlpha = false )
	{
		var vtfPath = "materials/" + texturePath.ToLowerInvariant().Replace( '\\', '/' );
		if ( !vtfPath.EndsWith( ".vtf" ) )
			vtfPath += ".vtf";

		// Check GMA first, then mount
		var data = _archive.ReadFile( vtfPath ) ?? _mount.ReadFile( vtfPath );
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
/// Resource loader for GMA-packaged sounds (.wav, .mp3)
/// </summary>
internal class GModSoundGma : ResourceLoader<GModMount>
{
	private readonly GModMount _mount;
	private readonly GmaArchive _archive;
	private readonly GmaEntry _entry;

	public GModSoundGma( GModMount mount, GmaArchive archive, GmaEntry entry )
	{
		_mount = mount;
		_archive = archive;
		_entry = entry;
	}

	protected override object Load()
	{
		var data = _archive.ReadFile( _entry );
		if ( data == null )
			return null;

		// Return raw sound data - s&box handles WAV/MP3 natively
		return data;
	}
}
