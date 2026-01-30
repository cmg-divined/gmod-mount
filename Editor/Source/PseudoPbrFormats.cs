using GModMount;

namespace GModMount.Source;

/// <summary>
/// Detected pseudo-PBR format type.
/// </summary>
public enum PbrFormat
{
	/// <summary>Standard Source Engine material (fallback)</summary>
	SourceEngine,
	
	/// <summary>ExoPBR - screenspace_general_8tex + ExoPBR proxy with ARM texture</summary>
	ExoPBR,
	
	/// <summary>GPBR (Strata Source) - "PBR" shader with MRAO texture</summary>
	GPBR,
	
	/// <summary>MWB PBR Gen - pow(gloss,4) encoding in exponent texture</summary>
	MWBPBR,
	
	/// <summary>BlueFlyTrap PseudoPBR - linear roughness in exponent, metallic in base alpha</summary>
	BFTPseudoPBR
}

/// <summary>
/// Extracted PBR properties from a material.
/// </summary>
public class ExtractedPbrProperties
{
	// Format detection
	public PbrFormat Format { get; set; } = PbrFormat.SourceEngine;
	
	// Common textures
	public string BaseTexturePath { get; set; }
	public string BumpMapPath { get; set; }
	public string EnvMapMaskPath { get; set; }
	public string PhongExponentTexturePath { get; set; }
	
	// ExoPBR specific
	public string ArmTexturePath { get; set; }  // $texture1 - AO/Roughness/Metallic/Height
	public string ExoNormalPath { get; set; }   // $texture2 - Normal map (DirectX Y-)
	public string EmissionTexturePath { get; set; } // $texture3 - Emission
	public float EmissionScale { get; set; } = 1f;
	
	// GPBR specific
	public string MraoTexturePath { get; set; } // $mraotexture - Metallic/Roughness/AO
	public string GpbrEmissionPath { get; set; } // $emissiontexture
	public float GpbrEmissionScale { get; set; } = 1f;
	
	// Common Source phong properties
	public float PhongExponent { get; set; } = 10f;
	public float PhongBoost { get; set; } = 1f;
	public bool HasPhong { get; set; }
	public bool HasEnvMap { get; set; }
	public float[] PhongFresnelRanges { get; set; }
	public float[] EnvMapTint { get; set; }
	
	// Calculated PBR values
	public float Roughness { get; set; } = 1f;
	public float Metallic { get; set; } = 0f;
	
	// Material flags
	public bool IsTranslucent { get; set; }
	public bool IsSelfIllum { get; set; }
	public bool IsSSBump { get; set; }
	public bool HasAlphaMetallic { get; set; } // BFT: metallic stored in base texture alpha
	
	// Alpha/transparency
	public bool IsAlphaTest { get; set; }
	public float AlphaTestReference { get; set; } = 0.5f;
	public float Alpha { get; set; } = 1f;
	public bool IsAdditive { get; set; }
	public bool IsNoCull { get; set; }
	
	// BFT specific
	public bool IsBftMetallicLayer { get; set; }
	public bool IsBftDiffuseLayer { get; set; }
	public float[] BftColor2 { get; set; }
}

/// <summary>
/// Format detection and PBR extraction utilities.
/// </summary>
public static class PseudoPbrFormats
{
	/// <summary>
	/// Detect which pseudo-PBR format a VMT file uses.
	/// </summary>
	public static PbrFormat DetectFormat( VmtFile vmt )
	{
		// Force MWB processing if setting is enabled (takes priority)
		if ( GModSettings.ForceMwbProcessing )
		{
			Log.Info( $"ForceMwbProcessing is ON - forcing MWBPBR format" );
			return PbrFormat.MWBPBR;
		}
		
		// Force BFT processing if setting is enabled
		if ( GModSettings.ForceBftProcessing )
		{
			Log.Info( $"ForceBftProcessing is ON - forcing BFTPseudoPBR format" );
			return PbrFormat.BFTPseudoPBR;
		}
		
		var shader = vmt.Shader?.ToLowerInvariant() ?? "";
		
		// Check for ExoPBR first (most specific)
		if ( shader == "screenspace_general_8tex" )
		{
			// Look for ExoPBR proxy marker
			foreach ( var proxy in vmt.Proxies )
			{
				if ( proxy.Name.Contains( "exopbr", StringComparison.OrdinalIgnoreCase ) )
					return PbrFormat.ExoPBR;
			}
			// Also check raw content for ExoPBR mention
			if ( vmt.Parameters.Any( p => p.Value.Contains( "exopbr", StringComparison.OrdinalIgnoreCase ) ) )
				return PbrFormat.ExoPBR;
		}
		
		// Check for GPBR (Strata Source)
		if ( shader == "pbr" )
		{
			return PbrFormat.GPBR;
		}
		
		// Check for MWB PBR or BFT PseudoPBR (both use VertexlitGeneric)
		if ( shader == "vertexlitgeneric" || shader == "lightmappedgeneric" )
		{
			var expTex = vmt.GetString( "$phongexponenttexture" );
			if ( !string.IsNullOrEmpty( expTex ) )
			{
				// Check for MWB markers
				if ( IsMwbFormat( vmt, expTex ) )
					return PbrFormat.MWBPBR;
				
				// Check for BFT markers
				if ( IsBftFormat( vmt, expTex ) )
					return PbrFormat.BFTPseudoPBR;
			}
		}
		
		return PbrFormat.SourceEngine;
	}
	
	/// <summary>
	/// Check if material uses MWB PBR Gen format.
	/// </summary>
	private static bool IsMwbFormat( VmtFile vmt, string expTexture )
	{
		// Check base texture for _rgb suffix (MWB's naming convention)
		var baseTex = vmt.BaseTexture?.ToLowerInvariant() ?? "";
		if ( baseTex.EndsWith( "_rgb" ) )
			return true;
		
		// Check exponent texture for _e suffix with _rgb base (common MWB pattern)
		var expLower = expTexture.ToLowerInvariant();
		if ( expLower.EndsWith( "_e" ) && baseTex.EndsWith( "_rgb" ) )
			return true;
		
		// Check for MWB path patterns in any texture
		if ( baseTex.Contains( "pbr\\output" ) || baseTex.Contains( "pbr/output" ) ||
		     expLower.Contains( "pbr\\output" ) || expLower.Contains( "pbr/output" ) )
			return true;
		
		// Check for MWB-specific proxies
		foreach ( var proxy in vmt.Proxies )
		{
			var proxyName = proxy.Name.ToLowerInvariant();
			if ( proxyName.Contains( "mwenvmaptint" ) || proxyName.Contains( "arc9envmaptint" ) )
				return true;
		}
		
		return false;
	}
	
	/// <summary>
	/// Check if material uses BlueFlyTrap PseudoPBR format.
	/// </summary>
	private static bool IsBftFormat( VmtFile vmt, string expTexture )
	{
		var expLower = expTexture.ToLowerInvariant();
		
		// BFT uses _e suffix for exponent textures
		if ( expLower.EndsWith( "_e" ) )
			return true;
		
		// Strong BFT marker: $blendTintByBaseAlpha with dark $color2
		if ( vmt.GetBool( "$blendtintbybasealpha" ) )
		{
			var color2 = vmt.GetVector3( "$color2" );
			float brightness = color2.x + color2.y + color2.z;
			if ( brightness < 0.3f )
				return true;
		}
		
		// Check for high phongboost (BFT uses 3-25)
		float boost = vmt.GetFloat( "$phongboost" );
		if ( boost >= 3f && boost <= 25f )
			return true;
		
		// Check characteristic fresnel ranges
		var fresnel = vmt.GetVector3( "$phongfresnelranges" );
		if ( fresnel != default )
		{
			// Metallic pattern: [0.87 0.9 1.0]
			bool metallic = ApproxEqual( fresnel.x, 0.87f, 0.1f ) && 
			                ApproxEqual( fresnel.y, 0.9f, 0.1f ) && 
			                ApproxEqual( fresnel.z, 1.0f, 0.1f );
			// Dielectric pattern: [0.05 0.115 0.945]
			bool dielectric = fresnel.x < 0.2f && fresnel.y < 0.3f && fresnel.z > 0.8f;
			
			if ( metallic || dielectric )
				return true;
		}
		
		return false;
	}
	
	/// <summary>
	/// Extract PBR properties from a VMT file.
	/// </summary>
	public static ExtractedPbrProperties ExtractProperties( VmtFile vmt )
	{
		var props = new ExtractedPbrProperties();
		
		// Detect format first
		props.Format = DetectFormat( vmt );
		
		// Extract common properties
		props.BaseTexturePath = vmt.BaseTexture;
		props.BumpMapPath = vmt.BumpMap;
		props.EnvMapMaskPath = vmt.GetString( "$envmapmask" );
		props.PhongExponentTexturePath = vmt.GetString( "$phongexponenttexture" );
		props.IsTranslucent = vmt.Translucent;
		props.IsSelfIllum = vmt.GetBool( "$selfillum" );
		props.IsSSBump = vmt.GetBool( "$ssbump" );
		props.IsAlphaTest = vmt.GetBool( "$alphatest" );
		props.AlphaTestReference = vmt.GetFloat( "$alphatestreference", 0.5f );
		props.Alpha = vmt.GetFloat( "$alpha", 1f );
		props.IsAdditive = vmt.GetBool( "$additive" );
		props.IsNoCull = vmt.GetBool( "$nocull" );
		props.HasPhong = vmt.GetBool( "$phong" );
		
		// Debug: log nocull detection
		if ( props.IsNoCull )
			Log.Info( $"    Detected $nocull in VMT" );
		
		// Debug: check if nocull key exists
		if ( vmt.Parameters.ContainsKey( "$nocull" ) )
			Log.Info( $"    VMT has $nocull key = '{vmt.Parameters["$nocull"]}'" );
		props.HasEnvMap = !string.IsNullOrEmpty( vmt.EnvMap );
		props.PhongExponent = vmt.GetFloat( "$phongexponent", 10f );
		props.PhongBoost = vmt.GetFloat( "$phongboost", 1f );
		
		var fresnelVec = vmt.GetVector3( "$phongfresnelranges" );
		if ( fresnelVec != default )
			props.PhongFresnelRanges = new[] { fresnelVec.x, fresnelVec.y, fresnelVec.z };
		
		var envTintVec = vmt.GetVector3( "$envmaptint" );
		if ( envTintVec != default )
			props.EnvMapTint = new[] { envTintVec.x, envTintVec.y, envTintVec.z };
		
		// Extract format-specific properties
		switch ( props.Format )
		{
			case PbrFormat.ExoPBR:
				ExtractExoPbrProperties( vmt, props );
				break;
			case PbrFormat.GPBR:
				ExtractGpbrProperties( vmt, props );
				break;
			case PbrFormat.MWBPBR:
				ExtractMwbProperties( vmt, props );
				break;
			case PbrFormat.BFTPseudoPBR:
				ExtractBftProperties( vmt, props );
				break;
			case PbrFormat.SourceEngine:
			default:
				ExtractSourceProperties( vmt, props );
				break;
		}
		
		return props;
	}
	
	/// <summary>
	/// Extract ExoPBR-specific properties.
	/// </summary>
	private static void ExtractExoPbrProperties( VmtFile vmt, ExtractedPbrProperties props )
	{
		props.ArmTexturePath = vmt.GetString( "$texture1" );      // ARM map
		props.ExoNormalPath = vmt.GetString( "$texture2" );       // Normal map
		props.EmissionTexturePath = vmt.GetString( "$texture3" ); // Emission
		props.EmissionScale = vmt.GetFloat( "$emissionscale", 1f );
		
		// ExoPBR provides direct PBR values
		props.Roughness = 0.5f;
		props.Metallic = 0f;
	}
	
	/// <summary>
	/// Extract GPBR-specific properties.
	/// </summary>
	private static void ExtractGpbrProperties( VmtFile vmt, ExtractedPbrProperties props )
	{
		props.MraoTexturePath = vmt.GetString( "$mraotexture" );
		props.GpbrEmissionPath = vmt.GetString( "$emissiontexture" );
		props.GpbrEmissionScale = vmt.GetFloat( "$emissionscale", 1f );
		
		// GPBR uses MRAO texture
		props.Roughness = 0.5f;
		props.Metallic = 0f;
	}
	
	/// <summary>
	/// Extract MWB PBR-specific properties.
	/// </summary>
	private static void ExtractMwbProperties( VmtFile vmt, ExtractedPbrProperties props )
	{
		// MWB uses pow(gloss, 4.0) encoding
		props.Roughness = 0.5f;
		props.Metallic = 0f;
	}
	
	/// <summary>
	/// Extract BFT PseudoPBR-specific properties.
	/// </summary>
	private static void ExtractBftProperties( VmtFile vmt, ExtractedPbrProperties props )
	{
		// Detect layer type
		bool isTranslucent = vmt.Translucent;
		bool hasAlbedoTint = vmt.GetBool( "$phongalbedotint" );
		props.IsBftMetallicLayer = isTranslucent && hasAlbedoTint;
		
		// Check for $blendTintByBaseAlpha with dark $color2
		bool hasBlendTint = vmt.GetBool( "$blendtintbybasealpha" );
		var color2 = vmt.GetVector3( "$color2" );
		if ( color2 != default )
		{
			props.BftColor2 = new[] { color2.x, color2.y, color2.z };
			float brightness = color2.x + color2.y + color2.z;
			if ( hasBlendTint && brightness < 0.3f )
			{
				props.IsBftDiffuseLayer = true;
				props.HasAlphaMetallic = true; // Metallic is stored in base alpha
			}
		}
		
		// Set metallic based on layer type
		if ( props.IsBftMetallicLayer )
			props.Metallic = 0.9f;
		else if ( props.IsBftDiffuseLayer )
			props.Metallic = 0.5f; // Variable from alpha
		else
			props.Metallic = 0f;
		
		props.Roughness = 0.5f; // Will be extracted from exponent texture
	}
	
	/// <summary>
	/// Extract standard Source Engine properties and estimate PBR values.
	/// </summary>
	private static void ExtractSourceProperties( VmtFile vmt, ExtractedPbrProperties props )
	{
		// Calculate roughness from phong properties
		props.Roughness = CalculateRoughness( props );
		
		// Estimate metallic from envmap properties
		props.Metallic = EstimateMetallic( props );
	}
	
	/// <summary>
	/// Calculate roughness from Source Engine phong properties.
	/// </summary>
	public static float CalculateRoughness( ExtractedPbrProperties props )
	{
		if ( !props.HasPhong )
		{
			// No phong = fully rough
			return 1f;
		}
		
		// Base roughness from phong exponent
		float roughness = PhongExponentToRoughness( props.PhongExponent );
		
		// Adjust based on phong boost
		if ( props.PhongBoost > 1f )
		{
			// Higher boost = shinier = lower roughness
			roughness *= 1f / MathF.Sqrt( props.PhongBoost );
		}
		
		// Adjust for fresnel ranges if present
		if ( props.PhongFresnelRanges != null )
		{
			// Higher fresnel max = shinier at glancing angles
			float fresnelMax = props.PhongFresnelRanges[2];
			if ( fresnelMax > 0.5f )
			{
				roughness *= 1f - (fresnelMax - 0.5f) * 0.5f;
			}
		}
		
		return Math.Clamp( roughness, 0.04f, 1f );
	}
	
	/// <summary>
	/// Estimate metallic value from Source Engine envmap properties.
	/// </summary>
	public static float EstimateMetallic( ExtractedPbrProperties props )
	{
		if ( !props.HasEnvMap )
			return 0f;
		
		// Check envmap tint brightness
		if ( props.EnvMapTint != null )
		{
			float tintBrightness = (props.EnvMapTint[0] + props.EnvMapTint[1] + props.EnvMapTint[2]) / 3f;
			
			// High envmap tint suggests metallic surface
			if ( tintBrightness > 0.5f )
				return Math.Min( tintBrightness, 0.9f );
		}
		
		// If phong fresnel ranges suggest metallic
		if ( props.PhongFresnelRanges != null )
		{
			// Metallic fresnel pattern: high minimum fresnel
			if ( props.PhongFresnelRanges[0] > 0.5f )
				return 0.8f;
		}
		
		return 0f;
	}
	
	/// <summary>
	/// Convert Source Engine phong exponent to PBR roughness.
	/// </summary>
	public static float PhongExponentToRoughness( float phongExponent )
	{
		// Phong exponent: higher = sharper/shinier highlight
		// Common values: 5 (matte) to 150+ (mirror-like)
		// Formula derived from Beckmann-Phong equivalence
		if ( phongExponent <= 0 )
			return 1f;
		
		// sqrt(2 / (phongExp + 2)) approximation
		float roughness = MathF.Sqrt( 2f / (phongExponent + 2f) );
		return Math.Clamp( roughness, 0.04f, 1f );
	}
	
	/// <summary>
	/// Convert BFT exponent texture value to roughness.
	/// BFT encoding: stored = pow(gloss, 1/0.28) ≈ pow(gloss, 3.57)
	/// Decoding: gloss = pow(stored, 0.28), roughness = 1 - gloss
	/// </summary>
	public static float BftExponentToRoughness( byte expValue )
	{
		// Normalize
		float normalized = expValue / 255f;
		// BFT uses Photoshop Levels with input mid 0.28
		// This means stored = pow(gloss, 1/0.28), so to decode: gloss = pow(stored, 0.28)
		float gloss = MathF.Pow( normalized, 0.28f );
		float roughness = 1f - gloss;
		return Math.Clamp( roughness, 0.04f, 1f );
	}
	
	/// <summary>
	/// Convert MWB exponent texture value to roughness.
	/// MWB uses pow(gloss, 4.0) encoding.
	/// </summary>
	public static float MwbExponentToRoughness( byte expValue )
	{
		// Normalize
		float normalized = expValue / 255f;
		// Inverse of pow(x, 4) = pow(x, 0.25)
		float gloss = MathF.Pow( normalized, 0.25f );
		float roughness = 1f - gloss;
		return Math.Clamp( roughness, 0.04f, 1f );
	}
	
	/// <summary>
	/// Helper for approximate float comparison.
	/// </summary>
	private static bool ApproxEqual( float a, float b, float epsilon = 0.1f )
	{
		return MathF.Abs( a - b ) < epsilon;
	}
}

/// <summary>
/// Texture generation utilities for PBR.
/// </summary>
public static class PbrTextureGenerator
{
	/// <summary>
	/// Extract roughness texture from ARM (AO/Roughness/Metallic) format.
	/// Roughness is in the Green channel.
	/// </summary>
	public static byte[] ExtractRoughnessFromArm( byte[] rgba, int width, int height )
	{
		var output = new byte[width * height * 4];
		int pixels = width * height;
		
		for ( int i = 0; i < pixels; i++ )
		{
			byte roughness = rgba[i * 4 + 1]; // Green channel
			output[i * 4 + 0] = roughness;
			output[i * 4 + 1] = roughness;
			output[i * 4 + 2] = roughness;
			output[i * 4 + 3] = 255;
		}
		
		return output;
	}
	
	/// <summary>
	/// Extract metallic texture from ARM (AO/Roughness/Metallic) format.
	/// Metallic is in the Blue channel.
	/// </summary>
	public static byte[] ExtractMetallicFromArm( byte[] rgba, int width, int height )
	{
		var output = new byte[width * height * 4];
		int pixels = width * height;
		
		for ( int i = 0; i < pixels; i++ )
		{
			byte metallic = rgba[i * 4 + 2]; // Blue channel
			output[i * 4 + 0] = metallic;
			output[i * 4 + 1] = metallic;
			output[i * 4 + 2] = metallic;
			output[i * 4 + 3] = 255;
		}
		
		return output;
	}
	
	/// <summary>
	/// Extract AO texture from ARM format.
	/// AO is in the Red channel.
	/// </summary>
	public static byte[] ExtractAoFromArm( byte[] rgba, int width, int height )
	{
		var output = new byte[width * height * 4];
		int pixels = width * height;
		
		for ( int i = 0; i < pixels; i++ )
		{
			byte ao = rgba[i * 4 + 0]; // Red channel
			output[i * 4 + 0] = ao;
			output[i * 4 + 1] = ao;
			output[i * 4 + 2] = ao;
			output[i * 4 + 3] = 255;
		}
		
		return output;
	}
	
	/// <summary>
	/// Extract roughness from MRAO (Metallic/Roughness/AO) format.
	/// Roughness is in the Green channel.
	/// </summary>
	public static byte[] ExtractRoughnessFromMrao( byte[] rgba, int width, int height )
	{
		// Same as ARM - roughness in green
		return ExtractRoughnessFromArm( rgba, width, height );
	}
	
	/// <summary>
	/// Extract metallic from MRAO format.
	/// Metallic is in the Red channel.
	/// </summary>
	public static byte[] ExtractMetallicFromMrao( byte[] rgba, int width, int height )
	{
		var output = new byte[width * height * 4];
		int pixels = width * height;
		
		for ( int i = 0; i < pixels; i++ )
		{
			byte metallic = rgba[i * 4 + 0]; // Red channel (different from ARM!)
			output[i * 4 + 0] = metallic;
			output[i * 4 + 1] = metallic;
			output[i * 4 + 2] = metallic;
			output[i * 4 + 3] = 255;
		}
		
		return output;
	}
	
	/// <summary>
	/// Extract metallic from base texture alpha channel (BFT format).
	/// </summary>
	public static byte[] ExtractMetallicFromAlpha( byte[] rgba, int width, int height )
	{
		var output = new byte[width * height * 4];
		int pixels = width * height;
		
		// Check if alpha has meaningful variation
		byte minAlpha = 255, maxAlpha = 0;
		for ( int i = 0; i < pixels; i += 8 )
		{
			byte a = rgba[i * 4 + 3];
			if ( a < minAlpha ) minAlpha = a;
			if ( a > maxAlpha ) maxAlpha = a;
		}
		
		// If alpha is uniform, return null (no metallic data)
		if ( maxAlpha - minAlpha < 10 )
			return null;
		
		for ( int i = 0; i < pixels; i++ )
		{
			byte metallic = rgba[i * 4 + 3]; // Alpha channel
			output[i * 4 + 0] = metallic;
			output[i * 4 + 1] = metallic;
			output[i * 4 + 2] = metallic;
			output[i * 4 + 3] = 255;
		}
		
		return output;
	}
	
	/// <summary>
	/// Convert BFT exponent texture to roughness texture.
	/// </summary>
	public static byte[] ConvertBftExponentToRoughness( byte[] rgba, int width, int height )
	{
		var output = new byte[width * height * 4];
		int pixels = width * height;
		
		for ( int i = 0; i < pixels; i++ )
		{
			byte expValue = rgba[i * 4 + 0]; // Red channel has inverted roughness
			float roughness = PseudoPbrFormats.BftExponentToRoughness( expValue );
			byte roughByte = (byte)(roughness * 255f);
			
			output[i * 4 + 0] = roughByte;
			output[i * 4 + 1] = roughByte;
			output[i * 4 + 2] = roughByte;
			output[i * 4 + 3] = 255;
		}
		
		return output;
	}
	
	/// <summary>
	/// Convert MWB exponent texture to roughness texture.
	/// MWB stores roughness with pow(gloss, 4.0) encoding.
	/// </summary>
	public static byte[] ConvertMwbExponentToRoughness( byte[] rgba, int width, int height )
	{
		var output = new byte[width * height * 4];
		int pixels = width * height;
		
		for ( int i = 0; i < pixels; i++ )
		{
			byte expValue = rgba[i * 4 + 0]; // Red channel
			float roughness = PseudoPbrFormats.MwbExponentToRoughness( expValue );
			byte roughByte = (byte)(roughness * 255f);
			
			output[i * 4 + 0] = roughByte;
			output[i * 4 + 1] = roughByte;
			output[i * 4 + 2] = roughByte;
			output[i * 4 + 3] = 255;
		}
		
		return output;
	}
	
	/// <summary>
	/// Extract metallic from MWB format (green channel of exponent texture).
	/// </summary>
	public static byte[] ExtractMwbMetallic( byte[] rgba, int width, int height )
	{
		var output = new byte[width * height * 4];
		int pixels = width * height;
		
		for ( int i = 0; i < pixels; i++ )
		{
			byte metallic = rgba[i * 4 + 1]; // Green channel
			output[i * 4 + 0] = metallic;
			output[i * 4 + 1] = metallic;
			output[i * 4 + 2] = metallic;
			output[i * 4 + 3] = 255;
		}
		
		return output;
	}
	
	/// <summary>
	/// Convert Source phong exponent to roughness texture.
	/// Uses the phong exponent texture or a constant value.
	/// </summary>
	public static byte[] ConvertPhongExponentToRoughness( byte[] rgba, int width, int height )
	{
		var output = new byte[width * height * 4];
		int pixels = width * height;
		
		for ( int i = 0; i < pixels; i++ )
		{
			// Phong exponent texture: higher value = shinier = lower roughness
			byte expValue = rgba[i * 4 + 0]; // Typically grayscale
			
			// Map 0-255 to phong exponent range (roughly 1-150)
			float phongExp = 1f + (expValue / 255f) * 149f;
			float roughness = PseudoPbrFormats.PhongExponentToRoughness( phongExp );
			byte roughByte = (byte)(roughness * 255f);
			
			output[i * 4 + 0] = roughByte;
			output[i * 4 + 1] = roughByte;
			output[i * 4 + 2] = roughByte;
			output[i * 4 + 3] = 255;
		}
		
		return output;
	}
	
	/// <summary>
	/// Flip normal map green channel (DirectX Y- to OpenGL Y+).
	/// Used for ExoPBR normal maps which use DirectX convention.
	/// </summary>
	public static byte[] FlipNormalMapGreen( byte[] rgba, int width, int height )
	{
		var output = new byte[rgba.Length];
		Array.Copy( rgba, output, rgba.Length );
		
		int pixels = width * height;
		for ( int i = 0; i < pixels; i++ )
		{
			output[i * 4 + 1] = (byte)(255 - output[i * 4 + 1]); // Flip green
		}
		
		return output;
	}
	
	/// <summary>
	/// Generate a constant roughness texture.
	/// </summary>
	public static byte[] GenerateConstantRoughness( int width, int height, float roughness )
	{
		byte value = (byte)(Math.Clamp( roughness, 0f, 1f ) * 255f);
		var output = new byte[width * height * 4];
		
		for ( int i = 0; i < width * height; i++ )
		{
			output[i * 4 + 0] = value;
			output[i * 4 + 1] = value;
			output[i * 4 + 2] = value;
			output[i * 4 + 3] = 255;
		}
		
		return output;
	}
	
	/// <summary>
	/// Generate a constant metallic texture.
	/// </summary>
	public static byte[] GenerateConstantMetallic( int width, int height, float metallic )
	{
		byte value = (byte)(Math.Clamp( metallic, 0f, 1f ) * 255f);
		var output = new byte[width * height * 4];
		
		for ( int i = 0; i < width * height; i++ )
		{
			output[i * 4 + 0] = value;
			output[i * 4 + 1] = value;
			output[i * 4 + 2] = value;
			output[i * 4 + 3] = 255;
		}
		
		return output;
	}
}
