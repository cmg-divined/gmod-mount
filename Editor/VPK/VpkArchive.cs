namespace GModMount.VPK;

/// <summary>
/// Reads and provides access to VPK (Valve PacK) archives.
/// Supports VPK version 1 and 2 (GMod uses v2).
/// </summary>
public sealed class VpkArchive : IDisposable
{
	private const uint VPK_SIGNATURE = 0x55AA1234;
	private const ushort DIR_ARCHIVE_INDEX = 0x7FFF;
	private const ushort ENTRY_TERMINATOR = 0xFFFF;

	private readonly string _basePath;
	private readonly string _directoryPath;
	private readonly Dictionary<string, VpkEntry> _entries = new( StringComparer.OrdinalIgnoreCase );
	private readonly Dictionary<ushort, string> _archivePaths = new();
	private readonly object _lock = new();

	private FileStream _directoryStream;
	private bool _disposed;

	/// <summary>
	/// VPK format version (1 or 2)
	/// </summary>
	public int Version { get; private set; }

	/// <summary>
	/// Size of the directory tree in bytes
	/// </summary>
	public uint TreeSize { get; private set; }

	/// <summary>
	/// Offset where embedded file data begins (in directory file)
	/// </summary>
	public uint EmbeddedDataOffset { get; private set; }

	/// <summary>
	/// Number of files in the archive
	/// </summary>
	public int FileCount => _entries.Count;

	/// <summary>
	/// All file entries in the archive
	/// </summary>
	public IEnumerable<VpkEntry> Entries => _entries.Values;

	/// <summary>
	/// Whether the archive was loaded successfully
	/// </summary>
	public bool IsValid { get; private set; }

	/// <summary>
	/// Opens a VPK archive from the directory file path.
	/// </summary>
	/// <param name="directoryFilePath">Path to the *_dir.vpk file</param>
	public VpkArchive( string directoryFilePath )
	{
		_directoryPath = directoryFilePath;

		// Derive base path (remove _dir.vpk suffix)
		if ( directoryFilePath.EndsWith( "_dir.vpk", StringComparison.OrdinalIgnoreCase ) )
		{
			_basePath = directoryFilePath[..^8]; // Remove "_dir.vpk"
		}
		else
		{
			_basePath = Path.ChangeExtension( directoryFilePath, null );
		}

		Load();
	}

	private void Load()
	{
		try
		{
			_directoryStream = File.OpenRead( _directoryPath );
			using var reader = new BinaryReader( _directoryStream, Encoding.ASCII, leaveOpen: true );

			// Read signature
			uint signature = reader.ReadUInt32();
			if ( signature != VPK_SIGNATURE )
			{
				Log.Warning( $"VPK: Invalid signature 0x{signature:X8}, expected 0x{VPK_SIGNATURE:X8}" );
				IsValid = false;
				return;
			}

			// Read version
			Version = reader.ReadInt32();
			if ( Version != 1 && Version != 2 )
			{
				Log.Warning( $"VPK: Unsupported version {Version}" );
				IsValid = false;
				return;
			}

			// Read tree size
			TreeSize = reader.ReadUInt32();

			// Version 2 has additional header fields
			uint fileDataSectionSize = 0;
			if ( Version == 2 )
			{
				fileDataSectionSize = reader.ReadUInt32();      // FileDataSectionSize
				reader.ReadUInt32();                             // ArchiveMD5SectionSize
				reader.ReadUInt32();                             // OtherMD5SectionSize
				reader.ReadUInt32();                             // SignatureSectionSize
			}

			// Calculate embedded data offset
			uint headerSize = Version == 1 ? 12u : 28u;
			EmbeddedDataOffset = headerSize + TreeSize;

			// Read directory tree
			ReadDirectoryTree( reader );

			// Discover archive files
			DiscoverArchives();

			IsValid = true;
			Log.Info( $"VPK: Loaded {_directoryPath} (v{Version}, {FileCount} files)" );
		}
		catch ( Exception ex )
		{
			Log.Error( $"VPK: Failed to load {_directoryPath}: {ex.Message}" );
			IsValid = false;
		}
	}

	private void ReadDirectoryTree( BinaryReader reader )
	{
		// Tree structure: Extension -> Path -> Filename
		while ( true )
		{
			string extension = ReadNullTerminatedString( reader );
			if ( string.IsNullOrEmpty( extension ) )
				break;

			while ( true )
			{
				string path = ReadNullTerminatedString( reader );
				if ( string.IsNullOrEmpty( path ) )
					break;

				while ( true )
				{
					string filename = ReadNullTerminatedString( reader );
					if ( string.IsNullOrEmpty( filename ) )
						break;

					// Read file entry info
					uint crc = reader.ReadUInt32();
					ushort preloadBytes = reader.ReadUInt16();
					ushort archiveIndex = reader.ReadUInt16();
					uint entryOffset = reader.ReadUInt32();
					uint entryLength = reader.ReadUInt32();
					ushort terminator = reader.ReadUInt16();

					if ( terminator != ENTRY_TERMINATOR )
					{
						Log.Warning( $"VPK: Unexpected terminator 0x{terminator:X4} for {path}/{filename}.{extension}" );
					}

					// Read preload data if present
					byte[] preloadData = preloadBytes > 0
						? reader.ReadBytes( preloadBytes )
						: Array.Empty<byte>();

					var entry = new VpkEntry(
						extension,
						path,
						filename,
						crc,
						preloadBytes,
						archiveIndex,
						entryOffset,
						entryLength,
						preloadData
					);

					_entries[entry.FullPath] = entry;
				}
			}
		}
	}

	private void DiscoverArchives()
	{
		// Find all archive files matching the base path pattern
		string directory = Path.GetDirectoryName( _basePath ) ?? ".";
		string baseFileName = Path.GetFileName( _basePath );

		if ( !System.IO.Directory.Exists( directory ) )
			return;

		foreach ( string file in System.IO.Directory.GetFiles( directory, $"{baseFileName}_*.vpk" ) )
		{
			string fileName = Path.GetFileNameWithoutExtension( file );
			string suffix = fileName[(baseFileName.Length + 1)..];

			// Skip the directory file
			if ( suffix.Equals( "dir", StringComparison.OrdinalIgnoreCase ) )
				continue;

			// Parse archive index
			if ( ushort.TryParse( suffix, out ushort archiveIndex ) )
			{
				_archivePaths[archiveIndex] = file;
			}
		}
	}

	private static string ReadNullTerminatedString( BinaryReader reader )
	{
		var sb = new StringBuilder();
		char c;
		while ( (c = reader.ReadChar()) != '\0' )
		{
			sb.Append( c );
		}
		return sb.ToString();
	}

	/// <summary>
	/// Checks if a file exists in the archive.
	/// </summary>
	public bool ContainsFile( string path )
	{
		return _entries.ContainsKey( NormalizePath( path ) );
	}

	/// <summary>
	/// Gets a file entry by path.
	/// </summary>
	public VpkEntry GetEntry( string path )
	{
		_entries.TryGetValue( NormalizePath( path ), out var entry );
		return entry;
	}

	/// <summary>
	/// Gets all entries matching a file extension.
	/// </summary>
	public IEnumerable<VpkEntry> GetEntriesByExtension( string extension )
	{
		extension = extension.TrimStart( '.' ).ToLowerInvariant();
		return _entries.Values.Where( e => e.Extension.Equals( extension, StringComparison.OrdinalIgnoreCase ) );
	}

	/// <summary>
	/// Gets all entries in a directory.
	/// </summary>
	public IEnumerable<VpkEntry> GetEntriesInDirectory( string directory, bool recursive = false )
	{
		directory = NormalizePath( directory ).TrimEnd( '/' );

		foreach ( var entry in _entries.Values )
		{
			if ( recursive )
			{
				if ( entry.DirectoryPath.StartsWith( directory, StringComparison.OrdinalIgnoreCase ) )
					yield return entry;
			}
			else
			{
				if ( entry.DirectoryPath.Equals( directory, StringComparison.OrdinalIgnoreCase ) )
					yield return entry;
			}
		}
	}

	/// <summary>
	/// Reads the raw bytes of a file from the archive.
	/// </summary>
	public byte[] ReadFile( string path )
	{
		var entry = GetEntry( path );
		return entry != null ? ReadFile( entry ) : null;
	}

	/// <summary>
	/// Reads the raw bytes of a file entry from the archive.
	/// </summary>
	public byte[] ReadFile( VpkEntry entry )
	{
		if ( _disposed || !IsValid )
			return null;

		try
		{
			byte[] data = new byte[entry.TotalLength];
			int offset = 0;

			// Copy preload data first
			if ( entry.PreloadBytes > 0 && entry.PreloadData != null )
			{
				Buffer.BlockCopy( entry.PreloadData, 0, data, 0, entry.PreloadBytes );
				offset = entry.PreloadBytes;
			}

			// Read remaining data from archive
			if ( entry.EntryLength > 0 )
			{
				byte[] archiveData = ReadFromArchive( entry );
				if ( archiveData != null )
				{
					Buffer.BlockCopy( archiveData, 0, data, offset, archiveData.Length );
				}
				else
				{
					return null;
				}
			}

			return data;
		}
		catch ( Exception ex )
		{
			Log.Error( $"VPK: Failed to read {entry.FullPath}: {ex.Message}" );
			return null;
		}
	}

	private byte[] ReadFromArchive( VpkEntry entry )
	{
		lock ( _lock )
		{
			if ( entry.IsInDirectoryFile )
			{
				// Data is in the directory file
				if ( _directoryStream == null )
					return null;

				_directoryStream.Seek( EmbeddedDataOffset + entry.EntryOffset, SeekOrigin.Begin );
				byte[] data = new byte[entry.EntryLength];
				_directoryStream.ReadExactly( data, 0, (int)entry.EntryLength );
				return data;
			}
			else
			{
				// Data is in a separate archive file
				if ( !_archivePaths.TryGetValue( entry.ArchiveIndex, out string archivePath ) )
				{
					Log.Warning( $"VPK: Archive {entry.ArchiveIndex} not found for {entry.FullPath}" );
					return null;
				}

				using var archiveStream = File.OpenRead( archivePath );
				archiveStream.Seek( entry.EntryOffset, SeekOrigin.Begin );
				byte[] data = new byte[entry.EntryLength];
				archiveStream.ReadExactly( data, 0, (int)entry.EntryLength );
				return data;
			}
		}
	}

	/// <summary>
	/// Opens a stream to read a file from the archive.
	/// </summary>
	public Stream OpenFile( string path )
	{
		byte[] data = ReadFile( path );
		return data != null ? new MemoryStream( data ) : null;
	}

	/// <summary>
	/// Opens a stream to read a file entry from the archive.
	/// </summary>
	public Stream OpenFile( VpkEntry entry )
	{
		byte[] data = ReadFile( entry );
		return data != null ? new MemoryStream( data ) : null;
	}

	private static string NormalizePath( string path )
	{
		return path.Replace( '\\', '/' ).ToLowerInvariant().TrimStart( '/' );
	}

	public void Dispose()
	{
		if ( _disposed )
			return;

		_disposed = true;
		_directoryStream?.Dispose();
		_directoryStream = null;
	}
}
