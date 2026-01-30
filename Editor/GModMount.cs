using GModMount.VPK;

namespace GModMount;

/// <summary>
/// A mounting implementation for Garry's Mod
/// </summary>
public partial class GModMount : BaseGameMount
{
	public override string Ident => "garrysmod";
	public override string Title => "Garry's Mod";

	const long AppId = 4000;

	readonly List<VpkArchive> _vpkArchives = [];
	readonly List<GmaArchive> _gmaArchives = [];
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
		string[] searchDirs =
		[
			Path.Combine( _gmodPath, "garrysmod" ),
			Path.Combine( _gmodPath, "sourceengine" ),
			Path.Combine( _gmodPath, "platform" ),
		];

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

		Log.Info( $"GModMount: Loaded {_vpkArchives.Count} VPK archives, {_gmaArchives.Count} GMA addons" );
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
