using GModMount.VPK;

namespace GModMount;

/// <summary>
/// Resource loader for Garry's Mod sounds (.wav, .mp3)
/// </summary>
internal class GModSound( GModMount mount, VpkEntry entry ) : ResourceLoader<GModMount>
{
	private readonly GModMount _mount = mount;
	private readonly VpkEntry _entry = entry;

	protected override object Load()
	{
		var data = _mount.ReadFile( _entry.FullPath );
		if ( data == null )
			return null;

		// Return raw sound data - s&box handles WAV/MP3 natively
		return data;
	}
}
