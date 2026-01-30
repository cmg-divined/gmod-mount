using GModMount.Source;
using GModMount.VPK;

namespace GModMount;

/// <summary>
/// Resource loader for Garry's Mod materials (.vmt)
/// </summary>
internal class GModMaterial( GModMount mount, VpkEntry entry ) : ResourceLoader<GModMount>
{
	private readonly GModMount _mount = mount;
	private readonly VpkEntry _entry = entry;

	// Default textures - created once and reused
	private static Texture _defaultNormal;
	private static Texture _defaultRoughness;

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

	/// <summary>
	/// Create a new material with the complex shader and default PBR settings.
	/// </summary>
	private static Material CreateMaterial( string name, Texture colorTexture )
	{
		var material = Material.Create( name, "complex" );
		
		// Set textures using correct property names for complex shader
		material.Set( "g_tColor", colorTexture );
		material.Set( "g_tNormal", DefaultNormal );
		material.Set( "g_tRoughness", DefaultRoughness );
		
		// Set additional texture slots to defaults to prevent checkered patterns
		material.Set( "g_tAmbientOcclusion", Texture.White );
		material.Set( "g_tTintMask", Texture.White );
		material.Set( "g_tSelfIllumMask", Texture.Black );
		
		// Source engine materials don't have PBR properties
		// Metalness 0 = non-metallic, Roughness 1 = fully matte (no shine)
		material.Set( "g_flMetalness", 0f );
		material.Set( "g_flRoughnessScaleFactor", 1f );
		material.Set( "g_flSelfIllumScale", 0f );
		
		return material;
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
			

			// Check if material is transparent (uses alpha for transparency)
			bool isTransparent = vmt.Translucent || vmt.AlphaTest;
			
			// Load base texture first
			// Force opaque alpha for non-transparent materials (they may use alpha for phong masks, etc.)
			Texture colorTexture = Texture.White;
			var baseTexturePath = vmt.BaseTexture;
			if ( !string.IsNullOrEmpty( baseTexturePath ) )
			{
				var loadedTexture = LoadTexture( baseTexturePath, forceOpaqueAlpha: !isTransparent );
				if ( loadedTexture != null && loadedTexture.IsValid() )
				{
					colorTexture = loadedTexture;
				}
			}

			// Create material with the color texture
			// Use a clean name without the .vmt extension
			var materialName = _entry.FullPath.Replace( ".vmt", "" ).Replace( '\\', '/' );
			var material = CreateMaterial( materialName, colorTexture );

			// Set bump map if available
			Texture normalTexture = DefaultNormal;
			var bumpMap = vmt.BumpMap;
			if ( !string.IsNullOrEmpty( bumpMap ) && !bumpMap.Contains( "null", StringComparison.OrdinalIgnoreCase ) )
			{
				var texture = LoadTexture( bumpMap );
				if ( texture != null && texture.IsValid() )
				{
					normalTexture = texture;
					material.Set( "g_tNormal", texture );
				}
			}

			return material;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"GModMaterial: Failed to load {_entry.FullPath}: {ex.Message}" );
			return null;
		}
	}

	/// <summary>
	/// Load a texture from the mount, normalizing the path.
	/// Loads the VTF directly instead of using Texture.Load to ensure we get the actual texture.
	/// </summary>
	/// <param name="textureName">Texture path from VMT</param>
	/// <param name="forceOpaqueAlpha">If true, forces alpha to 255 (for non-transparent materials)</param>
	private Texture LoadTexture( string textureName, bool forceOpaqueAlpha = false )
	{
		if ( string.IsNullOrEmpty( textureName ) )
			return null;

		// Normalize path: replace backslashes with forward slashes, lowercase
		var normalizedPath = textureName.Replace( '\\', '/' ).ToLowerInvariant().Trim( '/' );
		
		// Texture is registered with path like "materials/models/chairs/armchair"
		// $basetexture in VMT is typically "models/chairs/armchair" (without materials/ prefix)
		// So we need to prepend "materials/" to match registration
		var vtfPath = $"materials/{normalizedPath}.vtf";
		
		// Read the VTF file directly from the VPK
		var data = _mount.ReadFile( vtfPath );
		if ( data == null )
		{
			Log.Warning( $"GModMaterial.LoadTexture: VTF not found at '{vtfPath}'" );
			return null;
		}
		
		try
		{
			// Parse VTF file
			var vtf = VtfFile.Load( data );
			
			// Convert to RGBA (optionally forcing alpha to 255)
			var rgbaData = vtf.ConvertToRGBA( forceOpaqueAlpha );
			if ( rgbaData == null )
			{
				Log.Warning( $"GModMaterial.LoadTexture: Failed to convert VTF to RGBA" );
				return null;
			}
			
			// Create s&box texture directly
			var texture = Texture.Create( vtf.Width, vtf.Height )
				.WithData( rgbaData )
				.WithMips()
				.Finish();
			
			return texture;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"GModMaterial.LoadTexture: Failed to parse VTF '{vtfPath}': {ex.Message}" );
			return null;
		}
	}

	private static string GetShaderName( string vmtShader )
	{
		// Map Source shaders to s&box shaders
		return vmtShader?.ToLowerInvariant() switch
		{
			"lightmappedgeneric" => "complex",
			"vertexlitgeneric" => "complex",
			"unlitgeneric" => "unlit",
			"unlittwotexture" => "unlit",
			"water" => "water",
			"refract" => "glass",
			"eyerefract" => "complex",
			"eyes" => "complex",
			"skin" => "complex",
			"teeth" => "complex",
			_ => "complex"
		};
	}
}
