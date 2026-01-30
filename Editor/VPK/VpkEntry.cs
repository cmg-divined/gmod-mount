namespace GModMount.VPK;

/// <summary>
/// Represents a single file entry within a VPK archive.
/// </summary>
public sealed class VpkEntry
{
	/// <summary>
	/// Full path of the file (e.g., "materials/brick/brickwall001a.vmt")
	/// </summary>
	public string FullPath { get; }

	/// <summary>
	/// File extension without the dot (e.g., "vmt")
	/// </summary>
	public string Extension { get; }

	/// <summary>
	/// Directory path (e.g., "materials/brick")
	/// </summary>
	public string DirectoryPath { get; }

	/// <summary>
	/// File name without extension (e.g., "brickwall001a")
	/// </summary>
	public string FileName { get; }

	/// <summary>
	/// CRC32 checksum of the file data
	/// </summary>
	public uint Crc { get; }

	/// <summary>
	/// Number of bytes stored as preload data in the directory file
	/// </summary>
	public ushort PreloadBytes { get; }

	/// <summary>
	/// Index of the archive containing this file's data.
	/// 0x7FFF means the data is stored in the directory file itself.
	/// </summary>
	public ushort ArchiveIndex { get; }

	/// <summary>
	/// Offset of the file data within the archive (or directory file if ArchiveIndex is 0x7FFF)
	/// </summary>
	public uint EntryOffset { get; }

	/// <summary>
	/// Length of file data stored in the archive. If zero, entire file is in preload data.
	/// </summary>
	public uint EntryLength { get; }

	/// <summary>
	/// Preload data bytes (stored inline in directory for quick access to small/critical files)
	/// </summary>
	public byte[] PreloadData { get; }

	/// <summary>
	/// Total size of the file (PreloadBytes + EntryLength)
	/// </summary>
	public uint TotalLength => PreloadBytes + EntryLength;

	/// <summary>
	/// Whether this file's data is stored in the directory file rather than a separate archive
	/// </summary>
	public bool IsInDirectoryFile => ArchiveIndex == 0x7FFF;

	internal VpkEntry(
		string extension,
		string directoryPath,
		string fileName,
		uint crc,
		ushort preloadBytes,
		ushort archiveIndex,
		uint entryOffset,
		uint entryLength,
		byte[] preloadData )
	{
		Extension = extension;
		DirectoryPath = directoryPath;
		FileName = fileName;
		Crc = crc;
		PreloadBytes = preloadBytes;
		ArchiveIndex = archiveIndex;
		EntryOffset = entryOffset;
		EntryLength = entryLength;
		PreloadData = preloadData;

		// Build full path
		if ( string.IsNullOrEmpty( directoryPath ) || directoryPath == " " )
		{
			FullPath = string.IsNullOrEmpty( extension ) || extension == " "
				? fileName
				: $"{fileName}.{extension}";
		}
		else
		{
			FullPath = string.IsNullOrEmpty( extension ) || extension == " "
				? $"{directoryPath}/{fileName}"
				: $"{directoryPath}/{fileName}.{extension}";
		}
	}

	public override string ToString() => FullPath;
}
