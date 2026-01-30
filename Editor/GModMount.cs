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

	readonly List<VpkArchive> _archives = [];
	string _gmodPath;

	protected override void Initialize( InitializeContext context )
	{
		if ( !context.IsAppInstalled( AppId ) )
			return;

		_gmodPath = context.GetAppDirectory( AppId );
		if ( string.IsNullOrEmpty( _gmodPath ) || !System.IO.Directory.Exists( _gmodPath ) )
			return;

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
						_archives.Add( archive );
						MountVpkContents( context, archive );
						Log.Info( $"VPK: Loaded {vpkPath} (v{archive.Version}, {archive.FileCount} files)" );
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

		Log.Info( $"GModMount: Loaded {_archives.Count} VPK archives" );
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

	internal bool FileExists( string path )
	{
		path = path.ToLowerInvariant().Replace( '\\', '/' );

		foreach ( var archive in _archives )
		{
			if ( archive.ContainsFile( path ) )
				return true;
		}

		return false;
	}

	internal byte[] ReadFile( string path )
	{
		path = path.ToLowerInvariant().Replace( '\\', '/' );

		foreach ( var archive in _archives )
		{
			var entry = archive.GetEntry( path );
			if ( entry != null )
				return archive.ReadFile( entry );
		}

		return null;
	}

	internal VpkEntry GetEntry( string path )
	{
		path = path.ToLowerInvariant().Replace( '\\', '/' );

		foreach ( var archive in _archives )
		{
			var entry = archive.GetEntry( path );
			if ( entry != null )
				return entry;
		}

		return null;
	}

	protected override void Shutdown()
	{
		foreach ( var archive in _archives )
			archive.Dispose();

		_archives.Clear();
	}
}
