using GModMount.Source;
using GModMount.VPK;

namespace GModMount;

/// <summary>
/// Resource loader for Garry's Mod materials (.vmt)
/// Supports pseudo-PBR formats: ExoPBR, GPBR, MWB, BFT, and standard Source Engine.
/// </summary>
internal class GModMaterial : ResourceLoader<GModMount>
{
	private readonly GModMount _mount;
	private readonly VpkEntry _entry;

	public GModMaterial( GModMount mount, VpkEntry entry )
	{
		_mount = mount;
		_entry = entry;
	}

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
			
			// Detect format and extract PBR properties (pass path for path-based detection)
			var pbrProps = PseudoPbrFormats.ExtractProperties( vmt, _entry.FullPath );
			
			// Log detected format
			Log.Info( $"GModMaterial: {_entry.FullPath} -> {pbrProps.Format} (shader: {vmt.Shader})" );
			
			// Create material based on detected format
			var materialName = _entry.FullPath.Replace( ".vmt", "" ).Replace( '\\', '/' );
			
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
		var material = Material.Create( name, "shaders/gmod_pbr.shader" );
		
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
		material.Set( "g_tMetalness", metallicTexture );
		material.Set( "g_flMetalnessScale", 1f );
		material.Set( "g_tAmbientOcclusion", aoTexture );
		
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
		var material = Material.Create( name, "shaders/gmod_pbr.shader" );
		
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
		material.Set( "g_tMetalness", metallicTexture );
		material.Set( "g_flMetalnessScale", 1f );
		material.Set( "g_tAmbientOcclusion", aoTexture );
		
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
					
					// Make normal map alpha opaque (green flip disabled for testing)
					for ( int i = 0; i < rgba.Length; i += 4 )
					{
						// rgba[i + 1] = (byte)(255 - rgba[i + 1]); // Flip green - DISABLED
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

	/// <summary>
	/// Create material from BlueFlyTrap PseudoPBR format.
	/// BFT uses pow(0.28) roughness encoding in exponent texture, metallic in base alpha.
	/// </summary>
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
						Log.Info( $"    MadIvan18: roughness from normal map alpha (update) (inverted from gloss)" );
						
						// Flip green channel for normal map (DirectX to OpenGL)
						// DISABLED FOR TESTING
						for ( int i = 0; i < pixels; i++ )
						{
							// rgba[i * 4 + 1] = (byte)(255 - rgba[i * 4 + 1]); // Flip green - DISABLED
							rgba[i * 4 + 3] = 255;
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
							byte metal = rgba[i * 4 + 0];
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
		
		// Alpha test
		if ( props.IsAlphaTest && !needsTranslucency )
			material.Set( "g_flAlphaTestReference", props.AlphaTestReference );
		
		// Translucent or additive
		if ( needsTranslucency )
			material.Set( "g_flOpacity", props.Alpha );
		
		// Apply color tinting
		ApplyColorTinting( material, props );
		
		return material;
	}

	/// <summary>
	/// Create material from standard Source Engine format.
	/// Estimates PBR values from phong and envmap properties.
	/// </summary>
	private Material CreateSourceMaterial( string name, ExtractedPbrProperties props )
	{
		// Choose shader based on transparency and culling needs
		bool needsTranslucency = props.IsTranslucent || props.IsAdditive;
		string shaderPath = GetShaderPath( needsTranslucency, props.IsNoCull );
		var material = Material.Create( name, shaderPath );
		
		// Load base color texture - preserve alpha for alpha test, translucent, and additive materials
		bool needsAlpha = props.IsAlphaTest || needsTranslucency;
		var colorTexture = LoadTexture( props.BaseTexturePath, forceOpaqueAlpha: !needsAlpha ) ?? Texture.White;
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
		material.Set( "g_tMetalness", Texture.Black );
		material.Set( "g_flMetalnessScale", props.Metallic );
		material.Set( "g_flRoughnessScaleFactor", props.Roughness );
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
		
		// g_tIris - main iris/sclera texture
		if ( !string.IsNullOrEmpty( props.IrisTexturePath ) )
			material.Set( "g_tColor", LoadTexture( props.IrisTexturePath ) ?? Texture.White );
		else
			material.Set( "g_tColor", Texture.White );
		
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
		
		// Default eye projection vectors
		material.Set( "g_vEyeOrigin", Vector3.Zero );
		material.Set( "g_vIrisProjectionU", new Vector4( 0.0f, 0.05f, 0.0f, 0.5f ) );
		material.Set( "g_vIrisProjectionV", new Vector4( 0.0f, 0.0f, 0.05f, 0.5f ) );
		
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
			
			if ( props.BlendTintByBaseAlpha )
				material.Set( "g_flBlendTintByBaseAlpha", 1f );
		}
	}

	/// <summary>
	/// Load a normal map, flipping green channel for DirectX to OpenGL conversion.
	/// Source Engine uses DirectX convention (Y+ down), s&box uses OpenGL (Y+ up).
	/// </summary>
	private Texture LoadNormalMap( string path, bool isSSBump )
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
			// DirectX: Y+ points down (green 128=flat, >128=down, <128=up)
			// OpenGL: Y+ points up (opposite)
			// DISABLED FOR TESTING
			// for ( int i = 0; i < rgba.Length; i += 4 )
			//	rgba[i + 1] = (byte)(255 - rgba[i + 1]); // Flip green channel

			return CreateTexture( rgba, vtf.Width, vtf.Height );
		}
		catch
		{
			return DefaultNormal;
		}
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
