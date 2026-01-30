using GModMount.Source;
using GModMount.VPK;

namespace GModMount;

/// <summary>
/// Resource loader for Garry's Mod materials (.vmt)
/// Supports pseudo-PBR formats: ExoPBR, GPBR, MWB, BFT, and standard Source Engine.
/// </summary>
internal class GModMaterial( GModMount mount, VpkEntry entry ) : ResourceLoader<GModMount>
{
	private readonly GModMount _mount = mount;
	private readonly VpkEntry _entry = entry;

	// Default textures - created once and reused
	private static Texture _defaultNormal;
	private static Texture _defaultRoughness;
	private static Texture _defaultMetallic;
	private static Texture _defaultAo;

	private static Texture DefaultNormal
	{
		get
		{
			if ( _defaultNormal == null )
			{
				// Flat normal (128, 128, 255)
				_defaultNormal = Texture.Create( 1, 1 )
					.WithData( new byte[] { 128, 128, 255, 255 } )
					.Finish();
			}
			return _defaultNormal;
		}
	}

	private static Texture DefaultRoughness
	{
		get
		{
			if ( _defaultRoughness == null )
			{
				// Fully rough (white = 1.0 roughness)
				_defaultRoughness = Texture.Create( 1, 1 )
					.WithData( new byte[] { 255, 255, 255, 255 } )
					.Finish();
			}
			return _defaultRoughness;
		}
	}

	private static Texture DefaultMetallic
	{
		get
		{
			if ( _defaultMetallic == null )
			{
				// Non-metallic (black = 0.0 metallic)
				_defaultMetallic = Texture.Create( 1, 1 )
					.WithData( new byte[] { 0, 0, 0, 255 } )
					.Finish();
			}
			return _defaultMetallic;
		}
	}

	private static Texture DefaultAo
	{
		get
		{
			if ( _defaultAo == null )
			{
				// Full AO (white = no occlusion)
				_defaultAo = Texture.Create( 1, 1 )
					.WithData( new byte[] { 255, 255, 255, 255 } )
					.Finish();
			}
			return _defaultAo;
		}
	}

	protected override object Load()
	{
		try
		{
			var data = _mount.ReadFile( _entry.FullPath );
			if ( data == null )
			{
				Log.Warning( $"GModMaterial: Failed to read file {_entry.FullPath}" );
				return null;
			}

			// Parse VMT file
			string vmtContent = Encoding.UTF8.GetString( data );
			var vmt = VmtFile.Load( vmtContent );
			
			// Detect format and extract PBR properties
			var pbrProps = PseudoPbrFormats.ExtractProperties( vmt );
			
			// Log detected format
			Log.Info( $"GModMaterial: {_entry.FullPath} -> {pbrProps.Format} (shader: {vmt.Shader})" );
			
			// Create material based on detected format
			var materialName = _entry.FullPath.Replace( ".vmt", "" ).Replace( '\\', '/' );
			
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
			Log.Warning( $"GModMaterial: Failed to load {_entry.FullPath}: {ex.Message}" );
			return null;
		}
	}

	/// <summary>
	/// Create material from ExoPBR format.
	/// ExoPBR has direct PBR textures: ARM map, normal, emission.
	/// </summary>
	private Material CreateExoPbrMaterial( string name, ExtractedPbrProperties props )
	{
		var material = Material.Create( name, "complex" );
		
		// Load base color texture
		var colorTexture = LoadTexture( props.BaseTexturePath, forceOpaqueAlpha: true ) ?? Texture.White;
		material.Set( "g_tColor", colorTexture );
		
		// Load and process ARM texture (AO/Roughness/Metallic)
		Texture roughnessTexture = DefaultRoughness;
		Texture metallicTexture = DefaultMetallic;
		Texture aoTexture = DefaultAo;
		
		if ( !string.IsNullOrEmpty( props.ArmTexturePath ) )
		{
			var armData = LoadTextureRaw( props.ArmTexturePath );
			if ( armData != null )
			{
				var vtf = VtfFile.Load( armData );
				var rgba = vtf.ConvertToRGBA();
				
				if ( rgba != null )
				{
					// Extract channels from ARM
					var roughData = PbrTextureGenerator.ExtractRoughnessFromArm( rgba, vtf.Width, vtf.Height );
					var metalData = PbrTextureGenerator.ExtractMetallicFromArm( rgba, vtf.Width, vtf.Height );
					var aoData = PbrTextureGenerator.ExtractAoFromArm( rgba, vtf.Width, vtf.Height );
					
					roughnessTexture = CreateTexture( roughData, vtf.Width, vtf.Height );
					metallicTexture = CreateTexture( metalData, vtf.Width, vtf.Height );
					aoTexture = CreateTexture( aoData, vtf.Width, vtf.Height );
				}
			}
		}
		
		material.Set( "g_tRoughness", roughnessTexture );
		material.Set( "g_tAmbientOcclusion", aoTexture );
		material.Set( "g_flMetalness", 0f ); // Using texture instead
		
		// Load normal map (ExoPBR uses DirectX Y- format, need to flip green)
		Texture normalTexture = DefaultNormal;
		if ( !string.IsNullOrEmpty( props.ExoNormalPath ) )
		{
			var normalData = LoadTextureRaw( props.ExoNormalPath );
			if ( normalData != null )
			{
				var vtf = VtfFile.Load( normalData );
				var rgba = vtf.ConvertToRGBA();
				if ( rgba != null )
				{
					// Flip green channel for DirectX to OpenGL conversion
					var flipped = PbrTextureGenerator.FlipNormalMapGreen( rgba, vtf.Width, vtf.Height );
					normalTexture = CreateTexture( flipped, vtf.Width, vtf.Height );
				}
			}
		}
		material.Set( "g_tNormal", normalTexture );
		
		// Handle emission
		if ( !string.IsNullOrEmpty( props.EmissionTexturePath ) )
		{
			var emissionTex = LoadTexture( props.EmissionTexturePath );
			if ( emissionTex != null )
			{
				material.Set( "g_tSelfIllumMask", emissionTex );
				material.Set( "g_flSelfIllumScale", props.EmissionScale );
			}
			else
			{
				material.Set( "g_tSelfIllumMask", Texture.Black );
				material.Set( "g_flSelfIllumScale", 0f );
			}
		}
		else
		{
			material.Set( "g_tSelfIllumMask", Texture.Black );
			material.Set( "g_flSelfIllumScale", 0f );
		}
		
		material.Set( "g_tTintMask", Texture.White );
		material.Set( "g_flRoughnessScaleFactor", 1f );
		
		return material;
	}

	/// <summary>
	/// Create material from GPBR (Strata Source) format.
	/// GPBR has MRAO texture (Metallic/Roughness/AO).
	/// </summary>
	private Material CreateGpbrMaterial( string name, ExtractedPbrProperties props )
	{
		var material = Material.Create( name, "complex" );
		
		// Load base color texture
		var colorTexture = LoadTexture( props.BaseTexturePath, forceOpaqueAlpha: true ) ?? Texture.White;
		material.Set( "g_tColor", colorTexture );
		
		// Load and process MRAO texture (Metallic/Roughness/AO)
		Texture roughnessTexture = DefaultRoughness;
		Texture metallicTexture = DefaultMetallic;
		Texture aoTexture = DefaultAo;
		
		if ( !string.IsNullOrEmpty( props.MraoTexturePath ) )
		{
			var mraoData = LoadTextureRaw( props.MraoTexturePath );
			if ( mraoData != null )
			{
				var vtf = VtfFile.Load( mraoData );
				var rgba = vtf.ConvertToRGBA();
				
				if ( rgba != null )
				{
					// Extract channels from MRAO
					var roughData = PbrTextureGenerator.ExtractRoughnessFromMrao( rgba, vtf.Width, vtf.Height );
					var metalData = PbrTextureGenerator.ExtractMetallicFromMrao( rgba, vtf.Width, vtf.Height );
					// AO is in blue channel for MRAO
					var aoData = PbrTextureGenerator.ExtractAoFromArm( rgba, vtf.Width, vtf.Height );
					
					roughnessTexture = CreateTexture( roughData, vtf.Width, vtf.Height );
					metallicTexture = CreateTexture( metalData, vtf.Width, vtf.Height );
					aoTexture = CreateTexture( aoData, vtf.Width, vtf.Height );
				}
			}
		}
		
		material.Set( "g_tRoughness", roughnessTexture );
		material.Set( "g_tAmbientOcclusion", aoTexture );
		material.Set( "g_flMetalness", 0f );
		
		// Load normal map
		var normalTexture = LoadNormalMap( props.BumpMapPath, props.IsSSBump );
		material.Set( "g_tNormal", normalTexture );
		
		// Handle emission
		if ( !string.IsNullOrEmpty( props.GpbrEmissionPath ) )
		{
			var emissionTex = LoadTexture( props.GpbrEmissionPath );
			if ( emissionTex != null )
			{
				material.Set( "g_tSelfIllumMask", emissionTex );
				material.Set( "g_flSelfIllumScale", props.GpbrEmissionScale );
			}
			else
			{
				material.Set( "g_tSelfIllumMask", Texture.Black );
				material.Set( "g_flSelfIllumScale", 0f );
			}
		}
		else
		{
			material.Set( "g_tSelfIllumMask", Texture.Black );
			material.Set( "g_flSelfIllumScale", 0f );
		}
		
		material.Set( "g_tTintMask", Texture.White );
		material.Set( "g_flRoughnessScaleFactor", 1f );
		
		return material;
	}

	/// <summary>
	/// Create material from MWB PBR Gen format.
	/// MWB uses pow(gloss, 4.0) encoding in exponent texture.
	/// </summary>
	private Material CreateMwbMaterial( string name, ExtractedPbrProperties props )
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
		
		// Set metallic texture
		if ( metallicTexture != null )
		{
			material.Set( "g_tMetalness", metallicTexture );
		}
		
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
					// MWB encoding: roughness is INVERTED to gloss, then pow(gloss, 2.5)
					// So normal alpha = pow(1-roughness, 2.5) = pow(gloss, 2.5)
					// To decode: gloss = pow(normalized, 0.4), roughness = 1 - gloss
					var roughData = new byte[vtf.Width * vtf.Height * 4];
					int pixels = vtf.Width * vtf.Height;
					for ( int i = 0; i < pixels; i++ )
					{
						byte encoded = rgba[i * 4 + 3]; // Alpha = pow(gloss, 2.5)
						float normalized = encoded / 255f;
						float gloss = MathF.Pow( normalized, 0.4f );
						float roughness = 1.0f - gloss;
						byte roughByte = (byte)Math.Clamp( roughness * 255f, 0f, 255f );
						roughData[i * 4 + 0] = roughByte;
						roughData[i * 4 + 1] = roughByte;
						roughData[i * 4 + 2] = roughByte;
						roughData[i * 4 + 3] = 255;
					}
					roughnessTexture = CreateTexture( roughData, vtf.Width, vtf.Height );
					
					// Make normal map alpha opaque
					for ( int i = 3; i < rgba.Length; i += 4 )
						rgba[i] = 255;
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

	/// <summary>
	/// Create material from BlueFlyTrap PseudoPBR format.
	/// BFT uses linear roughness encoding in exponent texture, metallic in base alpha.
	/// </summary>
	private Material CreateBftMaterial( string name, ExtractedPbrProperties props )
	{
		var material = Material.Create( name, "complex" );
		
		// Load base color texture
		// For BFT, we need the alpha channel for metallic extraction
		Texture colorTexture = Texture.White;
		Texture metallicTexture = DefaultMetallic;
		
		if ( !string.IsNullOrEmpty( props.BaseTexturePath ) )
		{
			var baseData = LoadTextureRaw( props.BaseTexturePath );
			if ( baseData != null )
			{
				var vtf = VtfFile.Load( baseData );
				var rgba = vtf.ConvertToRGBA( forceOpaqueAlpha: false ); // Keep alpha!
				
				if ( rgba != null )
				{
					// Extract metallic from alpha channel if this is a BFT diffuse layer
					if ( props.HasAlphaMetallic )
					{
						var metalData = PbrTextureGenerator.ExtractMetallicFromAlpha( rgba, vtf.Width, vtf.Height );
						if ( metalData != null )
						{
							metallicTexture = CreateTexture( metalData, vtf.Width, vtf.Height );
						}
					}
					
					// Create color texture (with alpha set to 255 for rendering)
					for ( int i = 3; i < rgba.Length; i += 4 )
						rgba[i] = 255;
					
					colorTexture = CreateTexture( rgba, vtf.Width, vtf.Height );
				}
			}
		}
		
		material.Set( "g_tColor", colorTexture );
		material.Set( "g_flMetalness", props.IsBftMetallicLayer ? 0.9f : 0f );
		
		// Load and process exponent texture for roughness
		Texture roughnessTexture = DefaultRoughness;
		
		if ( !string.IsNullOrEmpty( props.PhongExponentTexturePath ) )
		{
			var expData = LoadTextureRaw( props.PhongExponentTexturePath );
			if ( expData != null )
			{
				var vtf = VtfFile.Load( expData );
				var rgba = vtf.ConvertToRGBA();
				
				if ( rgba != null )
				{
					// BFT: simple linear encoding in red channel
					var roughData = PbrTextureGenerator.ConvertBftExponentToRoughness( rgba, vtf.Width, vtf.Height );
					roughnessTexture = CreateTexture( roughData, vtf.Width, vtf.Height );
				}
			}
		}
		
		material.Set( "g_tRoughness", roughnessTexture );
		
		// Load normal map
		var normalTexture = LoadNormalMap( props.BumpMapPath, props.IsSSBump );
		material.Set( "g_tNormal", normalTexture );
		
		// Set defaults for other textures
		material.Set( "g_tAmbientOcclusion", DefaultAo );
		material.Set( "g_tTintMask", Texture.White );
		material.Set( "g_tSelfIllumMask", Texture.Black );
		material.Set( "g_flSelfIllumScale", 0f );
		material.Set( "g_flRoughnessScaleFactor", 1f );
		
		return material;
	}

	/// <summary>
	/// Create material from standard Source Engine format.
	/// Estimates PBR values from phong and envmap properties.
	/// </summary>
	private Material CreateSourceMaterial( string name, ExtractedPbrProperties props )
	{
		var material = Material.Create( name, "complex" );
		
		// Load base color texture
		bool isTransparent = props.IsTranslucent;
		var colorTexture = LoadTexture( props.BaseTexturePath, forceOpaqueAlpha: !isTransparent ) ?? Texture.White;
		material.Set( "g_tColor", colorTexture );
		
		// Load normal map
		var normalTexture = LoadNormalMap( props.BumpMapPath, props.IsSSBump );
		material.Set( "g_tNormal", normalTexture );
		
		// Try to generate roughness from phong exponent texture or envmap mask
		Texture roughnessTexture = DefaultRoughness;
		
		if ( !string.IsNullOrEmpty( props.PhongExponentTexturePath ) )
		{
			var expData = LoadTextureRaw( props.PhongExponentTexturePath );
			if ( expData != null )
			{
				var vtf = VtfFile.Load( expData );
				var rgba = vtf.ConvertToRGBA();
				
				if ( rgba != null )
				{
					var roughData = PbrTextureGenerator.ConvertPhongExponentToRoughness( rgba, vtf.Width, vtf.Height );
					roughnessTexture = CreateTexture( roughData, vtf.Width, vtf.Height );
				}
			}
		}
		else if ( !string.IsNullOrEmpty( props.EnvMapMaskPath ) )
		{
			// EnvMapMask can indicate where material is shiny (inverted = roughness)
			var maskData = LoadTextureRaw( props.EnvMapMaskPath );
			if ( maskData != null )
			{
				var vtf = VtfFile.Load( maskData );
				var rgba = vtf.ConvertToRGBA();
				
				if ( rgba != null )
				{
					// Invert: high mask = shiny = low roughness
					var roughData = new byte[vtf.Width * vtf.Height * 4];
					for ( int i = 0; i < vtf.Width * vtf.Height; i++ )
					{
						byte inverted = (byte)(255 - rgba[i * 4]);
						roughData[i * 4 + 0] = inverted;
						roughData[i * 4 + 1] = inverted;
						roughData[i * 4 + 2] = inverted;
						roughData[i * 4 + 3] = 255;
					}
					roughnessTexture = CreateTexture( roughData, vtf.Width, vtf.Height );
				}
			}
		}
		
		material.Set( "g_tRoughness", roughnessTexture );
		
		// Set calculated PBR values
		material.Set( "g_flMetalness", props.Metallic );
		material.Set( "g_flRoughnessScaleFactor", props.Roughness );
		
		// Set defaults for other textures
		material.Set( "g_tAmbientOcclusion", DefaultAo );
		material.Set( "g_tTintMask", Texture.White );
		
		// Handle self-illumination
		if ( props.IsSelfIllum )
		{
			material.Set( "g_tSelfIllumMask", Texture.White );
			material.Set( "g_flSelfIllumScale", 1f );
		}
		else
		{
			material.Set( "g_tSelfIllumMask", Texture.Black );
			material.Set( "g_flSelfIllumScale", 0f );
		}
		
		return material;
	}

	/// <summary>
	/// Load a normal map, handling SSBump conversion if needed.
	/// </summary>
	private Texture LoadNormalMap( string path, bool isSSBump )
	{
		if ( string.IsNullOrEmpty( path ) || path.Contains( "null", StringComparison.OrdinalIgnoreCase ) )
			return DefaultNormal;

		var texture = LoadTexture( path );
		if ( texture == null || !texture.IsValid() )
			return DefaultNormal;

		// Note: SSBump conversion would require additional processing
		// For now, just use the texture as-is (most normal maps work fine)
		return texture;
	}

	/// <summary>
	/// Load raw VTF file data (not converted to texture yet).
	/// </summary>
	private byte[] LoadTextureRaw( string textureName )
	{
		if ( string.IsNullOrEmpty( textureName ) )
			return null;

		var normalizedPath = textureName.Replace( '\\', '/' ).ToLowerInvariant().Trim( '/' );
		var vtfPath = $"materials/{normalizedPath}.vtf";
		
		return _mount.ReadFile( vtfPath );
	}

	/// <summary>
	/// Load a texture from the mount.
	/// </summary>
	private Texture LoadTexture( string textureName, bool forceOpaqueAlpha = false )
	{
		if ( string.IsNullOrEmpty( textureName ) )
			return null;

		var data = LoadTextureRaw( textureName );
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
		catch ( Exception ex )
		{
			Log.Warning( $"GModMaterial.LoadTexture: Failed to parse VTF: {ex.Message}" );
			return null;
		}
	}

	/// <summary>
	/// Create an s&box texture from RGBA data.
	/// </summary>
	private static Texture CreateTexture( byte[] rgba, int width, int height )
	{
		return Texture.Create( width, height )
			.WithData( rgba )
			.WithMips()
			.Finish();
	}

	/// <summary>
	/// Check if a grayscale texture has meaningful variation (not uniform).
	/// </summary>
	private static bool HasTextureVariation( byte[] rgba, int width, int height )
	{
		if ( rgba == null || rgba.Length < 4 )
			return false;

		byte minVal = 255;
		byte maxVal = 0;
		int pixels = width * height;
		
		// Sample every 8th pixel for speed
		for ( int i = 0; i < pixels; i += 8 )
		{
			byte val = rgba[i * 4]; // Red channel (grayscale)
			if ( val < minVal ) minVal = val;
			if ( val > maxVal ) maxVal = val;
		}

		// Variation threshold of 10 (out of 255) filters compression artifacts
		return (maxVal - minVal) > 10;
	}
}
