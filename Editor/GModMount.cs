using GModMount.VPK;

namespace GModMount;

/// <summary>
/// Settings for the GMod mount.
/// </summary>
public static class GModSettings
{
	/// <summary>
	/// When true, forces all materials to be processed as MWB PBR regardless of detection.
	/// Useful for content that uses MWB encoding but lacks detection markers.
	/// </summary>
	public static bool ForceMwbProcessing { get; set; } = false;
	
	/// <summary>
	/// When true, forces all materials to be processed as BlueFlyTrap PseudoPBR regardless of detection.
	/// Useful for content that uses BFT encoding but lacks detection markers.
	/// Note: ForceMwbProcessing takes priority if both are enabled.
	/// </summary>
	public static bool ForceBftProcessing { get; set; } = false;
	
	/// <summary>
	/// When true, forces all materials to be processed as MadIvan18 format regardless of path detection.
	/// MadIvan18 format: roughness in normal map alpha, metalness in exponent red channel.
	/// Note: ForceMwbProcessing and ForceBftProcessing take priority if enabled.
	/// </summary>
	public static bool ForceMadIvan18Processing { get; set; } = false;
}

/// <summary>
/// A mounting implementation for Garry's Mod
/// </summary>
public partial class GModMount : BaseGameMount
{
	public override string Ident => "garrysmod";
	public override string Title => "Garry's Mod";

	const long AppId = 4000;

	readonly List<VpkArchive> _vpkArchives = new List<VpkArchive>();
	readonly List<GmaArchive> _gmaArchives = new List<GmaArchive>();
	string _gmodPath;
	string _workshopPath;

	protected override void Initialize( InitializeContext context )
	{
		if ( !context.IsAppInstalled( AppId ) )
			return;

		_gmodPath = context.GetAppDirectory( AppId );
		if ( string.IsNullOrEmpty( _gmodPath ) || !System.IO.Directory.Exists( _gmodPath ) )
			return;

		// Workshop content is in steamapps/workshop/content/4000/
		// Go up from gmodPath (steamapps/common/GarrysMod) to steamapps, then into workshop
		var steamAppsPath = Path.GetDirectoryName( Path.GetDirectoryName( _gmodPath ) );
		if ( !string.IsNullOrEmpty( steamAppsPath ) )
		{
			_workshopPath = Path.Combine( steamAppsPath, "workshop", "content", AppId.ToString() );
		}

		IsInstalled = true;
	}

	protected override Task Mount( MountContext context )
	{
		if ( !IsInstalled )
			return Task.CompletedTask;

		// GMod has VPKs in multiple directories:
		// - garrysmod/ (main game content)
		// - sourceengine/ (HL2, CSS content)
		// - platform/ (shared Valve content)
		string[] searchDirs = new string[]
		{
			Path.Combine( _gmodPath, "garrysmod" ),
			Path.Combine( _gmodPath, "sourceengine" ),
			Path.Combine( _gmodPath, "platform" ),
		};

		// Load VPK archives
		foreach ( var searchDir in searchDirs )
		{
			if ( !System.IO.Directory.Exists( searchDir ) )
				continue;

			foreach ( var vpkPath in System.IO.Directory.EnumerateFiles( searchDir, "*_dir.vpk", SearchOption.AllDirectories ) )
			{
				try
				{
					var archive = new VpkArchive( vpkPath );
					if ( archive.IsValid )
					{
						_vpkArchives.Add( archive );
						MountVpkContents( context, archive );
					}
					else
					{
						archive.Dispose();
					}
				}
				catch ( Exception ex )
				{
					context.AddError( $"Failed to load VPK {vpkPath}: {ex.Message}" );
				}
			}
		}

		// Load GMA archives from workshop
		if ( !string.IsNullOrEmpty( _workshopPath ) && System.IO.Directory.Exists( _workshopPath ) )
		{
			foreach ( var gmaPath in System.IO.Directory.EnumerateFiles( _workshopPath, "*.gma", SearchOption.AllDirectories ) )
			{
				try
				{
					var archive = GmaArchive.Load( gmaPath );
					if ( archive != null )
					{
						_gmaArchives.Add( archive );
						MountGmaContents( context, archive );
						Log.Info( $"GMA: Loaded {gmaPath} ({archive.Entries.Count} files, \"{archive.AddonName}\")" );
					}
				}
				catch ( Exception ex )
				{
					context.AddError( $"Failed to load GMA {gmaPath}: {ex.Message}" );
				}
			}
		}

		// Also check garrysmod/addons for extracted or local GMA files
		var addonsPath = Path.Combine( _gmodPath, "garrysmod", "addons" );
		if ( System.IO.Directory.Exists( addonsPath ) )
		{
			foreach ( var gmaPath in System.IO.Directory.EnumerateFiles( addonsPath, "*.gma", SearchOption.AllDirectories ) )
			{
				try
				{
					var archive = GmaArchive.Load( gmaPath );
					if ( archive != null )
					{
						_gmaArchives.Add( archive );
						MountGmaContents( context, archive );
						Log.Info( $"GMA: Loaded {gmaPath} ({archive.Entries.Count} files, \"{archive.AddonName}\")" );
					}
				}
				catch ( Exception ex )
				{
					context.AddError( $"Failed to load GMA {gmaPath}: {ex.Message}" );
				}
			}
		}

		// Load loose files from garrysmod/models and garrysmod/materials (manually installed content)
		var looseFileCount = MountLooseFiles( context );

		Log.Info( $"GModMount: Loaded {_vpkArchives.Count} VPK archives, {_gmaArchives.Count} GMA addons, {looseFileCount} loose files" );
		IsMounted = true;
		return Task.CompletedTask;
	}

	private void MountVpkContents( MountContext context, VpkArchive archive )
	{
		foreach ( var entry in archive.Entries )
		{
			var ext = entry.Extension.ToLowerInvariant();
			var path = entry.FullPath;
			var pathWithoutExt = path[..^(ext.Length + 1)];

			switch ( ext )
			{
				case "mdl":
					context.Add( ResourceType.Model, pathWithoutExt, new GModModel( this, entry ) );
					break;
				case "vtf":
					context.Add( ResourceType.Texture, pathWithoutExt, new GModTexture( this, entry ) );
					break;
				case "vmt":
					context.Add( ResourceType.Material, pathWithoutExt, new GModMaterial( this, entry ) );
					break;
				case "wav":
				case "mp3":
					context.Add( ResourceType.Sound, pathWithoutExt, new GModSound( this, entry ) );
					break;
			}
		}
	}

	private void MountGmaContents( MountContext context, GmaArchive archive )
	{
		// Create a safe folder name from the addon name
		var addonFolder = SanitizeFolderName( archive.AddonName );
		if ( string.IsNullOrEmpty( addonFolder ) )
			addonFolder = "unnamed";

		foreach ( var entry in archive.Entries )
		{
			var path = entry.Path.ToLowerInvariant().Replace( '\\', '/' );
			var ext = Path.GetExtension( path ).TrimStart( '.' );
			
			if ( string.IsNullOrEmpty( ext ) )
				continue;

			var nativePath = path[..^(ext.Length + 1)];
			var addonPath = $"addons/{addonFolder}/" + nativePath;

			switch ( ext )
			{
				case "mdl":
					// Models go under addons/ for organization
					context.Add( ResourceType.Model, addonPath, new GModModelGma( this, archive, entry ) );
					break;
				case "vtf":
					// Textures at both paths - native for material loading, addons/ for browsing
					context.Add( ResourceType.Texture, nativePath, new GModTextureGma( this, archive, entry ) );
					context.Add( ResourceType.Texture, addonPath, new GModTextureGma( this, archive, entry ) );
					break;
				case "vmt":
					// Materials at both paths - native for model loading, addons/ for browsing
					context.Add( ResourceType.Material, nativePath, new GModMaterialGma( this, archive, entry ) );
					context.Add( ResourceType.Material, addonPath, new GModMaterialGma( this, archive, entry ) );
					break;
				case "wav":
				case "mp3":
					context.Add( ResourceType.Sound, addonPath, new GModSoundGma( this, archive, entry ) );
					break;
			}
		}
	}

	private int MountLooseFiles( MountContext context )
	{
		int count = 0;
		var gmodRoot = Path.Combine( _gmodPath, "garrysmod" );

		// Scan models folder
		var modelsPath = Path.Combine( gmodRoot, "models" );
		if ( System.IO.Directory.Exists( modelsPath ) )
		{
			foreach ( var filePath in System.IO.Directory.EnumerateFiles( modelsPath, "*.mdl", SearchOption.AllDirectories ) )
			{
				var relativePath = Path.GetRelativePath( gmodRoot, filePath ).Replace( '\\', '/' ).ToLowerInvariant();
				var pathWithoutExt = relativePath[..^4]; // Remove .mdl
				
				context.Add( ResourceType.Model, $"custom/{pathWithoutExt}", new GModModelLoose( this, filePath, gmodRoot ) );
				count++;
			}
		}

		// Scan materials folder
		var materialsPath = Path.Combine( gmodRoot, "materials" );
		if ( System.IO.Directory.Exists( materialsPath ) )
		{
			// VMT files
			foreach ( var filePath in System.IO.Directory.EnumerateFiles( materialsPath, "*.vmt", SearchOption.AllDirectories ) )
			{
				var relativePath = Path.GetRelativePath( gmodRoot, filePath ).Replace( '\\', '/' ).ToLowerInvariant();
				var pathWithoutExt = relativePath[..^4]; // Remove .vmt
				
				// Mount at both native and custom/ paths
				context.Add( ResourceType.Material, pathWithoutExt, new GModMaterialLoose( this, filePath, gmodRoot ) );
				context.Add( ResourceType.Material, $"custom/{pathWithoutExt}", new GModMaterialLoose( this, filePath, gmodRoot ) );
				count++;
			}

			// VTF files
			foreach ( var filePath in System.IO.Directory.EnumerateFiles( materialsPath, "*.vtf", SearchOption.AllDirectories ) )
			{
				var relativePath = Path.GetRelativePath( gmodRoot, filePath ).Replace( '\\', '/' ).ToLowerInvariant();
				var pathWithoutExt = relativePath[..^4]; // Remove .vtf
				
				// Mount at both native and custom/ paths
				context.Add( ResourceType.Texture, pathWithoutExt, new GModTextureLoose( this, filePath ) );
				context.Add( ResourceType.Texture, $"custom/{pathWithoutExt}", new GModTextureLoose( this, filePath ) );
				count++;
			}
		}

		// Scan sound folder
		var soundPath = Path.Combine( gmodRoot, "sound" );
		if ( System.IO.Directory.Exists( soundPath ) )
		{
			foreach ( var filePath in System.IO.Directory.EnumerateFiles( soundPath, "*.wav", SearchOption.AllDirectories ) )
			{
				var relativePath = Path.GetRelativePath( gmodRoot, filePath ).Replace( '\\', '/' ).ToLowerInvariant();
				var pathWithoutExt = relativePath[..^4];
				
				context.Add( ResourceType.Sound, $"custom/{pathWithoutExt}", new GModSoundLoose( filePath ) );
				count++;
			}
			foreach ( var filePath in System.IO.Directory.EnumerateFiles( soundPath, "*.mp3", SearchOption.AllDirectories ) )
			{
				var relativePath = Path.GetRelativePath( gmodRoot, filePath ).Replace( '\\', '/' ).ToLowerInvariant();
				var pathWithoutExt = relativePath[..^4];
				
				context.Add( ResourceType.Sound, $"custom/{pathWithoutExt}", new GModSoundLoose( filePath ) );
				count++;
			}
		}

		if ( count > 0 )
			Log.Info( $"Loose: Mounted {count} files from garrysmod/ folder" );

		return count;
	}

	internal string GetLooseFilePath( string relativePath )
	{
		var fullPath = Path.Combine( _gmodPath, "garrysmod", relativePath.Replace( '/', '\\' ) );
		return File.Exists( fullPath ) ? fullPath : null;
	}

	internal bool FileExists( string path )
	{
		path = path.ToLowerInvariant().Replace( '\\', '/' );

		foreach ( var archive in _vpkArchives )
		{
			if ( archive.ContainsFile( path ) )
				return true;
		}

		foreach ( var archive in _gmaArchives )
		{
			if ( archive.FindEntry( path ) != null )
				return true;
		}

		// Check loose files
		var loosePath = GetLooseFilePath( path );
		if ( loosePath != null )
			return true;

		return false;
	}

	internal byte[] ReadFile( string path )
	{
		path = path.ToLowerInvariant().Replace( '\\', '/' );

		// Check VPK archives first
		foreach ( var archive in _vpkArchives )
		{
			var entry = archive.GetEntry( path );
			if ( entry != null )
				return archive.ReadFile( entry );
		}

		// Then check GMA archives
		foreach ( var archive in _gmaArchives )
		{
			var entry = archive.FindEntry( path );
			if ( entry != null )
				return archive.ReadFile( entry );
		}

		// Finally check loose files
		var loosePath = GetLooseFilePath( path );
		if ( loosePath != null )
			return File.ReadAllBytes( loosePath );

		return null;
	}

	internal VpkEntry GetEntry( string path )
	{
		path = path.ToLowerInvariant().Replace( '\\', '/' );

		foreach ( var archive in _vpkArchives )
		{
			var entry = archive.GetEntry( path );
			if ( entry != null )
				return entry;
		}

		return null;
	}

	/// <summary>
	/// Sanitize a string to be safe for use as a folder name
	/// </summary>
	private static string SanitizeFolderName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return null;

		// Remove invalid path characters
		var invalid = Path.GetInvalidFileNameChars();
		var sanitized = new System.Text.StringBuilder();
		
		foreach ( var c in name )
		{
			if ( Array.IndexOf( invalid, c ) < 0 )
				sanitized.Append( c );
		}

		// Replace spaces with underscores, trim, and lowercase
		return sanitized.ToString().Trim().Replace( ' ', '_' ).ToLowerInvariant();
	}

	protected override void Shutdown()
	{
		foreach ( var archive in _vpkArchives )
			archive.Dispose();

		_vpkArchives.Clear();
		_gmaArchives.Clear();
	}
}
