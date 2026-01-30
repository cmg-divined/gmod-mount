// Compatibility shim for building outside of s&box

/// <summary>
/// Fallback Log implementation for standalone builds.
/// </summary>
internal static class Log
{
	public static void Info( string message ) => System.Console.WriteLine( $"[Info] {message}" );
	public static void Info( object message ) => Info( message?.ToString() ?? "null" );
	public static void Warning( string message ) => System.Console.WriteLine( $"[Warning] {message}" );
	public static void Warning( object message ) => Warning( message?.ToString() ?? "null" );
	public static void Error( string message ) => System.Console.WriteLine( $"[Error] {message}" );
	public static void Error( object message ) => Error( message?.ToString() ?? "null" );
}
