using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GModMount;

/// <summary>
/// Entry in a GMA archive
/// </summary>
public class GmaEntry
{
	public string Path { get; set; }
	public long Size { get; set; }
	public long DataOffset { get; set; }
	public uint Crc { get; set; }
}

/// <summary>
/// Garry's Mod Addon archive (.gma) reader
/// </summary>
public class GmaArchive
{
	public string FilePath { get; private set; }
	public string AddonName { get; private set; }
	public string AddonDescription { get; private set; }
	public string AddonAuthor { get; private set; }
	public uint AddonVersion { get; private set; }
	public byte FormatVersion { get; private set; }
	
	public List<GmaEntry> Entries { get; private set; } = new();
	
	private const string GMA_IDENT = "GMAD";
	
	public static GmaArchive Load( string filePath )
	{
		if ( !File.Exists( filePath ) )
			return null;
			
		var archive = new GmaArchive();
		archive.FilePath = filePath;
		
		using var stream = File.OpenRead( filePath );
		using var reader = new BinaryReader( stream, Encoding.ASCII );
		
		// Read and validate header
		var ident = new string( reader.ReadChars( 4 ) );
		if ( ident != GMA_IDENT )
		{
			Log.Warning( $"GmaArchive: Invalid GMA file '{filePath}' - bad identifier '{ident}'" );
			return null;
		}
		
		archive.FormatVersion = reader.ReadByte();
		
		// Skip steamID (8 bytes) and timestamp (8 bytes)
		reader.ReadBytes( 16 );
		
		// Version > 1 has required content field (list of null-terminated strings until empty)
		if ( archive.FormatVersion > 1 )
		{
			string content = ReadNullTerminatedString( reader );
			while ( !string.IsNullOrEmpty( content ) )
			{
				content = ReadNullTerminatedString( reader );
			}
		}
		
		// Read addon metadata
		archive.AddonName = ReadNullTerminatedString( reader );
		archive.AddonDescription = ReadNullTerminatedString( reader );
		archive.AddonAuthor = ReadNullTerminatedString( reader );
		archive.AddonVersion = reader.ReadUInt32();
		
		// Read file entries
		long dataOffset = 0;
		uint fileNumber = reader.ReadUInt32();
		
		while ( fileNumber != 0 )
		{
			var entry = new GmaEntry();
			entry.Path = ReadNullTerminatedString( reader );
			entry.Size = reader.ReadInt64();
			entry.Crc = reader.ReadUInt32();
			entry.DataOffset = dataOffset;
			
			archive.Entries.Add( entry );
			
			dataOffset += entry.Size;
			fileNumber = reader.ReadUInt32();
		}
		
		// Current position is where file data begins
		long fileDataStart = stream.Position;
		
		// Update all entry offsets to be absolute
		foreach ( var entry in archive.Entries )
		{
			entry.DataOffset += fileDataStart;
		}
		
		return archive;
	}
	
	/// <summary>
	/// Read file data for an entry
	/// </summary>
	public byte[] ReadFile( GmaEntry entry )
	{
		if ( entry == null || entry.Size <= 0 )
			return null;
			
		using var stream = File.OpenRead( FilePath );
		stream.Seek( entry.DataOffset, SeekOrigin.Begin );
		
		var data = new byte[entry.Size];
		stream.Read( data, 0, (int)entry.Size );
		return data;
	}
	
	/// <summary>
	/// Read file data by path
	/// </summary>
	public byte[] ReadFile( string path )
	{
		var entry = FindEntry( path );
		return entry != null ? ReadFile( entry ) : null;
	}
	
	/// <summary>
	/// Find entry by path (case-insensitive)
	/// </summary>
	public GmaEntry FindEntry( string path )
	{
		path = path.Replace( '\\', '/' ).ToLowerInvariant();
		
		foreach ( var entry in Entries )
		{
			if ( entry.Path.Replace( '\\', '/' ).ToLowerInvariant() == path )
				return entry;
		}
		
		return null;
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
}
