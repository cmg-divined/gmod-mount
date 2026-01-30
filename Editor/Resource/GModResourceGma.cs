using GModMount.Source;

namespace GModMount;

/// <summary>
/// Resource loader for GMA-packaged models (.mdl)
/// </summary>
internal class GModModelGma( GModMount mount, GmaArchive archive, GmaEntry entry ) : ResourceLoader<GModMount>
{
	private readonly GModMount _mount = mount;
	private readonly GmaArchive _archive = archive;
	private readonly GmaEntry _entry = entry;

	protected override object Load()
	{
		var basePath = _entry.Path[..^4];
		
		try
		{
			// Read MDL data from GMA
			var mdlData = _archive.ReadFile( _entry );
			if ( mdlData == null || mdlData.Length < 408 )
				return Model.Error;

			// Read VVD (check GMA first, then fall back to mount)
			var vvdPath = basePath + ".vvd";
			var vvdData = _archive.ReadFile( vvdPath ) ?? _mount.ReadFile( vvdPath );
			if ( vvdData == null )
				return Model.Error;

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
				return Model.Error;

			// Read PHY (optional)
			var phyPath = basePath + ".phy";
			var phyData = _archive.ReadFile( phyPath ) ?? _mount.ReadFile( phyPath );

			// Parse and convert
			var sourceModel = SourceModel.Load( mdlData, vvdData, vtxData, phyData );
			return SourceModelLoader.Convert( sourceModel, path: Path, mount: _mount );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"GModModelGma: Failed to load {_entry.Path}: {ex.Message}" );
			return Model.Error;
		}
	}
}

/// <summary>
/// Resource loader for GMA-packaged textures (.vtf)
/// </summary>
internal class GModTextureGma( GModMount mount, GmaArchive archive, GmaEntry entry ) : ResourceLoader<GModMount>
{
	private readonly GModMount _mount = mount;
	private readonly GmaArchive _archive = archive;
	private readonly GmaEntry _entry = entry;

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
/// Resource loader for GMA-packaged materials (.vmt)
/// </summary>
internal class GModMaterialGma( GModMount mount, GmaArchive archive, GmaEntry entry ) : ResourceLoader<GModMount>
{
	private readonly GModMount _mount = mount;
	private readonly GmaArchive _archive = archive;
	private readonly GmaEntry _entry = entry;

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
			var data = _archive.ReadFile( _entry );
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
			var materialName = _entry.Path.Replace( ".vmt", "" ).Replace( '\\', '/' );
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
			Log.Warning( $"GModMaterialGma: Failed to load {_entry.Path}: {ex.Message}" );
			return null;
		}
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
/// Resource loader for GMA-packaged sounds (.wav, .mp3)
/// </summary>
internal class GModSoundGma( GModMount mount, GmaArchive archive, GmaEntry entry ) : ResourceLoader<GModMount>
{
	private readonly GModMount _mount = mount;
	private readonly GmaArchive _archive = archive;
	private readonly GmaEntry _entry = entry;

	protected override object Load()
	{
		var data = _archive.ReadFile( _entry );
		if ( data == null )
			return null;

		// Return raw sound data - s&box handles WAV/MP3 natively
		return data;
	}
}
