using GModMount.Source;

namespace GModMount;

/// <summary>
/// Resource loader for loose model files (.mdl)
/// </summary>
internal class GModModelLoose( GModMount mount, string filePath, string rootPath ) : ResourceLoader<GModMount>
{
	private readonly GModMount _mount = mount;
	private readonly string _filePath = filePath;
	private readonly string _rootPath = rootPath;

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
			string[] vtxExtensions = [ ".dx90.vtx", ".dx80.vtx", ".sw.vtx" ];
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
internal class GModTextureLoose( GModMount mount, string filePath ) : ResourceLoader<GModMount>
{
	private readonly GModMount _mount = mount;
	private readonly string _filePath = filePath;

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
/// Resource loader for loose material files (.vmt)
/// </summary>
internal class GModMaterialLoose( GModMount mount, string filePath, string rootPath ) : ResourceLoader<GModMount>
{
	private readonly GModMount _mount = mount;
	private readonly string _filePath = filePath;
	private readonly string _rootPath = rootPath;

	private static Texture _defaultNormal;
	private static Texture DefaultNormal
	{
		get
		{
			if ( _defaultNormal == null )
			{
				_defaultNormal = Texture.Create( 1, 1 )
					.WithData( new byte[] { 128, 128, 255, 255 } )
					.Finish();
			}
			return _defaultNormal;
		}
	}

	protected override object Load()
	{
		try
		{
			var data = File.ReadAllBytes( _filePath );
			if ( data == null )
				return null;

			string vmtContent = System.Text.Encoding.UTF8.GetString( data );
			var vmt = VmtFile.Load( vmtContent );

			bool isTransparent = vmt.Translucent || vmt.AlphaTest;

			// Load base texture
			Texture colorTexture = Texture.White;
			var baseTexturePath = vmt.BaseTexture;
			if ( !string.IsNullOrEmpty( baseTexturePath ) )
			{
				var loadedTexture = LoadTexture( baseTexturePath, forceOpaqueAlpha: !isTransparent );
				if ( loadedTexture != null && loadedTexture.IsValid() )
					colorTexture = loadedTexture;
			}

			// Create material
			var relativePath = System.IO.Path.GetRelativePath( _rootPath, _filePath ).Replace( '\\', '/' );
			var materialName = relativePath.Replace( ".vmt", "" );
			var material = Material.Create( materialName, "complex.shader" );
			material.Set( "g_tColor", colorTexture );
			material.Set( "g_flMetalness", 0f );
			material.Set( "g_flRoughnessScaleFactor", 1f );
			material.Set( "g_flSelfIllumScale", 0f );
			material.Set( "g_tAmbientOcclusion", Texture.White );
			material.Set( "g_tTintMask", Texture.White );
			material.Set( "g_tSelfIllumMask", Texture.Black );

			// Set bump map if available
			var bumpMap = vmt.BumpMap;
			if ( !string.IsNullOrEmpty( bumpMap ) && !bumpMap.Contains( "null", StringComparison.OrdinalIgnoreCase ) )
			{
				var texture = LoadTexture( bumpMap );
				if ( texture != null && texture.IsValid() )
					material.Set( "g_tNormal", texture );
				else
					material.Set( "g_tNormal", DefaultNormal );
			}
			else
			{
				material.Set( "g_tNormal", DefaultNormal );
			}

			return material;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"GModMaterialLoose: Failed to load {_filePath}: {ex.Message}" );
			return null;
		}
	}

	private Texture LoadTexture( string texturePath, bool forceOpaqueAlpha = false )
	{
		var vtfRelPath = "materials/" + texturePath.ToLowerInvariant().Replace( '\\', '/' );
		if ( !vtfRelPath.EndsWith( ".vtf" ) )
			vtfRelPath += ".vtf";

		// Check loose files first
		var loosePath = System.IO.Path.Combine( _rootPath, vtfRelPath.Replace( '/', '\\' ) );
		byte[] data = null;
		
		if ( File.Exists( loosePath ) )
			data = File.ReadAllBytes( loosePath );
		else
			data = _mount.ReadFile( vtfRelPath );

		if ( data == null )
			return null;

		try
		{
			var vtf = VtfFile.Load( data );
			var rgbaData = vtf.ConvertToRGBA( forceOpaqueAlpha );
			if ( rgbaData == null )
				return null;

			return Texture.Create( vtf.Width, vtf.Height )
				.WithData( rgbaData )
				.WithMips()
				.Finish();
		}
		catch
		{
			return null;
		}
	}
}

/// <summary>
/// Resource loader for loose sound files (.wav, .mp3)
/// </summary>
internal class GModSoundLoose( string filePath ) : ResourceLoader<GModMount>
{
	private readonly string _filePath = filePath;

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
