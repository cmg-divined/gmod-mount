using GModMount.VPK;

namespace GModMount;

/// <summary>
/// Resource loader for Garry's Mod sounds (.wav, .mp3)
/// </summary>
internal class GModSound : ResourceLoader<GModMount>
{
	private readonly GModMount _mount;
	private readonly VpkEntry _entry;

	public GModSound( GModMount mount, VpkEntry entry )
	{
		_mount = mount;
		_entry = entry;
	}

	protected override object Load()
	{
		var data = _mount.ReadFile( _entry.FullPath );
		if ( data == null )
			return null;

		// Return raw sound data - s&box handles WAV/MP3 natively
		return data;
	}
}
