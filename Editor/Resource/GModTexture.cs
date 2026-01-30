using GModMount.Source;
using GModMount.VPK;

namespace GModMount;

/// <summary>
/// Resource loader for Garry's Mod textures (.vtf)
/// </summary>
internal class GModTexture( GModMount mount, VpkEntry entry ) : ResourceLoader<GModMount>
{
	private readonly GModMount _mount = mount;
	private readonly VpkEntry _entry = entry;

	protected override object Load()
	{
		try
		{
			Log.Info( $"GModTexture: Loading {_entry.FullPath}" );
			
			var data = _mount.ReadFile( _entry.FullPath );
			if ( data == null )
			{
				Log.Warning( $"GModTexture: Failed to read file {_entry.FullPath}" );
				return Texture.Invalid;
			}

			Log.Info( $"GModTexture: Read {data.Length} bytes" );

			// Parse VTF file
			var vtf = VtfFile.Load( data );
			Log.Info( $"GModTexture: VTF {vtf.Width}x{vtf.Height}, format={vtf.Format}" );

			// Convert to RGBA
			var rgbaData = vtf.ConvertToRGBA();
			if ( rgbaData == null )
			{
				Log.Warning( $"GModTexture: Failed to convert VTF to RGBA" );
				return Texture.Invalid;
			}

			Log.Info( $"GModTexture: Converted to RGBA, {rgbaData.Length} bytes" );

			// Create s&box texture
			var texture = Texture.Create( vtf.Width, vtf.Height )
				.WithData( rgbaData )
				.WithMips()
				.Finish();

			Log.Info( $"GModTexture: Created texture, valid={texture?.IsValid()}" );
			return texture;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"GModTexture: Failed to load {_entry.FullPath}: {ex.Message}" );
			return Texture.Invalid;
		}
	}
}
