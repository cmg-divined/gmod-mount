using GModMount.Source;
using GModMount.VPK;

namespace GModMount;

/// <summary>
/// Resource loader for Garry's Mod models (.mdl)
/// </summary>
internal class GModModel : ResourceLoader<GModMount>
{
	private readonly GModMount _mount;
	private readonly VpkEntry _entry;

	public GModModel( GModMount mount, VpkEntry entry )
	{
		_mount = mount;
		_entry = entry;
	}

	protected override object Load()
	{
		var basePath = _entry.FullPath[..^4];
		
		try
		{
			// Read MDL data
			Log.Info( $"GModModel: Loading {_entry.FullPath}" );
			var mdlData = _mount.ReadFile( _entry.FullPath );
			if ( mdlData == null || mdlData.Length < 408 )
			{
				Log.Warning( $"GModModel: MDL file missing or too small: {_entry.FullPath} ({mdlData?.Length ?? 0} bytes)" );
				return Model.Error;
			}
			Log.Info( $"GModModel: MDL data: {mdlData.Length} bytes" );

			// Read VVD
			var vvdPath = basePath + ".vvd";
			var vvdData = _mount.ReadFile( vvdPath );
			if ( vvdData == null )
			{
				Log.Warning( $"GModModel: VVD file not found: {vvdPath}" );
				return Model.Error;
			}
			Log.Info( $"GModModel: VVD data: {vvdData.Length} bytes" );

			// Read VTX (try dx90, dx80, sw in order)
			string vtxPath = basePath + ".dx90.vtx";
			var vtxData = _mount.ReadFile( vtxPath );
			if ( vtxData == null )
			{
				vtxPath = basePath + ".dx80.vtx";
				vtxData = _mount.ReadFile( vtxPath );
			}
			if ( vtxData == null )
			{
				vtxPath = basePath + ".sw.vtx";
				vtxData = _mount.ReadFile( vtxPath );
			}

			if ( vtxData == null )
			{
				Log.Warning( $"GModModel: VTX file not found: {basePath}.*.vtx" );
				return Model.Error;
			}
			Log.Info( $"GModModel: VTX data: {vtxData.Length} bytes ({vtxPath})" );

			// Read PHY (optional - physics/ragdoll data)
			var phyPath = basePath + ".phy";
			var phyData = _mount.ReadFile( phyPath );
			if ( phyData != null )
			{
				Log.Info( $"GModModel: PHY data: {phyData.Length} bytes" );
			}

			// Parse the Source model
			Log.Info( $"GModModel: Parsing MDL..." );
			var sourceModel = SourceModel.Load( mdlData, vvdData, vtxData, phyData );
			Log.Info( $"GModModel: Parsed - Version {sourceModel.Version}, {sourceModel.Mdl.Bones.Count} bones, {sourceModel.Mdl.BodyParts.Count} bodyparts" );
			if ( sourceModel.HasPhysics )
			{
				Log.Info( $"GModModel: Has physics - {sourceModel.Phy.CollisionData.Count} solids, {sourceModel.Phy.RagdollConstraints.Count} ragdoll constraints" );
			}

			// Convert to s&box model
			Log.Info( $"GModModel: Converting to s&box model..." );
			var result = SourceModelLoader.Convert( sourceModel, path: Path, mount: _mount );
			Log.Info( $"GModModel: Successfully loaded {_entry.FullPath}" );
			
			return result;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"GModModel: Failed to load {_entry.FullPath}: {ex.Message}" );
			Log.Warning( $"GModModel: Stack trace: {ex.StackTrace}" );
			return Model.Error;
		}
	}
}
