using BattleTech;
using BattleTech.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Sheepy.BattleTechMod.Turbine {
   using System.Collections;
   using System.Text.RegularExpressions;
   using static Mod;
   using static System.Reflection.BindingFlags;

   public class DataProcess : BattleModModule {

      public static void LogStart() { Info( "Start" ); }
      public static void LogEnd() { Info( "End" ); }

      public override void ModStarts () {
         Patch( typeof( HBS.Util.JSONSerializationUtility ), "StripHBSCommentsFromJSON", NonPublic | Static, "Override_StripComments", null );
         Patch( typeof( DataManager ), "GetDataHash", Static, "MultiThreadDataHash", null );
         Patch( typeof( CSVReader ), "ReadRow", new Type[]{}, "Override_CSVReader_ReadRow", null );
      }

      public static string Unescape ( string value ) {
         if ( value.StartsWith( "\"" ) && value.EndsWith( "\"" ) ) {
            value = value.Substring( 1, value.Length - 2 );
            if ( value.Contains( "\"\"" ) ) value = value.Replace( "\"\"", "\"" );
         }
         return value;
      }

      // ============ Json Process ============

      public static bool Override_StripComments ( ref String __result, String json ) { try {
         __result = StripComments( json );
         return false;
      }                 catch ( Exception ex ) { return Error( ex ); } }

      public static String StripComments ( String json ) {
         int pos = 0;
         StringBuilder buf = new StringBuilder( json.Length );
         do {
Loop:
            for ( int i = pos, len = json.Length - 2 ; i < len ; i++ ) {
               char a = json[ i ];
               if ( a == '/' ) { // Detect //* to */
                  char b = Peek( json, i+1 );
                  if ( b == '/' ) {
                     if ( Peek( json, i+2 ) == '*' ) { // //* to */
                        if ( SkipWS( buf, json, ref pos, i, i+3, "*/" ) ) goto Loop;
                     } /*else {                          // Single line comment // to \n, conflict with url string and requires string state tracking
                        if ( Skip( buf, json, ref pos, i, i+2, "\n" ) ) {
                           buf.Append( '\n' );
                           goto Loop;
                        }
                     }*/
                  } else if ( b == '*' ) { // /* to */
                     if ( SkipWS( buf, json, ref pos, i, i+2, "*/" ) ) goto Loop;
                  }
               } else if ( a == '<' && Match( json, i+1, "!--" ) ) { // <!-- to -->
                  if ( SkipWS( buf, json, ref pos, i, i+4, "-->" ) ) goto Loop;
               }
            }
            // Nothing found, copy everything and break
            buf.Append( json.Substring( pos ) );
            break;
         } while ( true );
         return buf.ToString();
      }

      private static bool Match ( String json, int pos, String txt ) {
         if ( json.Length <= pos + txt.Length ) return false;
         String sub = json.Substring( pos, txt.Length );
         return sub == txt;
      }
      private static bool SkipWS ( StringBuilder buf, String json, ref int pos, int skipStart, int headEnd, String until ) {
         if ( ! Skip( buf, json, ref pos, skipStart, headEnd, until ) ) return false;
         int len = json.Length;
         while ( pos < len ) {
            switch ( json[ pos ] ) {
               case ' ': case '\t': case '\r': case '\n':
                  pos++;
                  break;
               default:
                  return true;
            }
         }
         return true;
      }
      private static bool Skip ( StringBuilder buf, String json, ref int pos, int skipStart, int headEnd, String until ) {
         if ( json.Length <= headEnd ) return false;
         int tailStart = json.IndexOf( until, headEnd );
         if ( tailStart < 0 ) return false;
         if ( skipStart > 0 )
            buf.Append( json.Substring( pos, skipStart - pos ) );
         pos = tailStart + until.Length;
         return true;
      }
      private static char Peek ( String json, int pos ) {
         if ( json.Length <= pos ) return '\u0000';
         return json[ pos ];
      }

      // ============ Data Hash ============

      private static byte[] SecretKey;
      private const int JobPerLoop = 16;

      public static bool MultiThreadDataHash ( ref string __result, params BattleTechResourceType[] typesToHash ) { try {
         Verbo( "Prepare to get data hash." );
         if ( SecretKey == null ) {
            SecretKey = (byte[]) typeof( DataManager ).GetField( "secret_key", NonPublic | Static ).GetValue( null );
            if ( SecretKey == null ) throw new NullReferenceException( "DataManager.secret_key is null" );
         }

         int manifestCounter = 0;
         // For me, half the original time is spent on this new BattleTechResourceLocator, and most of that time is in CSVReader.ReadRow.
         BattleTechResourceLocator battleTechResourceLocator = new BattleTechResourceLocator(); 
         Dictionary<int,VersionManifestEntry> manifestList = new Dictionary<int,VersionManifestEntry>( 4000 ); // Vanilla has 900+. Mods may adds a lot more.
         foreach ( BattleTechResourceType type in typesToHash )
            foreach ( VersionManifestEntry versionManifestEntry in battleTechResourceLocator.AllEntriesOfResource( type ) )
               manifestList.Add( manifestCounter++, versionManifestEntry );

         Dictionary<int,byte[]> hashSet = new Dictionary<int,byte[]>();
         int threadCount = Math.Max( 2, Math.Min( Environment.ProcessorCount*2, 32 ) ), doneThread = 0;
         Info( "Calculating data hash with {0} threads.", threadCount );
         for ( int i = 0 ; i < threadCount ; i++ ) {
            new Thread( () => {
               HMACSHA256 hasher = new HMACSHA256( SecretKey );
               Dictionary<int,VersionManifestEntry> taskList = new Dictionary<int, VersionManifestEntry>( JobPerLoop );
               Dictionary<int,byte[]> resultList = new Dictionary<int,byte[]>();
               do {
                  lock( manifestList ) {
                     foreach ( var manifest in manifestList.Take( JobPerLoop ) ) taskList.Add( manifest.Key, manifest.Value );
                     foreach ( int id in taskList.Keys ) manifestList.Remove( id );
                  }
                  if ( taskList.Count <= 0 ) break;
                  foreach ( var task in taskList ) try {
                     VersionManifestEntry versionManifestEntry = task.Value;
                     if ( versionManifestEntry.IsAssetBundled || versionManifestEntry.IsResourcesAsset || ! File.Exists( versionManifestEntry.FilePath ) )
                        continue;
                     using ( FileStream fileStream = new FileStream( versionManifestEntry.FilePath, FileMode.Open, FileAccess.Read ) ) {
                        resultList.Add( task.Key, hasher.ComputeHash( fileStream ) );
                     }
                  } catch ( Exception ex ) {
                     Error( "Cannot hash {0}: {1}", task.Value.FilePath, ex );
                  }
                  taskList.Clear();
               } while ( true );
               lock( hashSet ) { 
                  foreach ( var result in resultList ) hashSet.Add( result.Key, result.Value );
                  ++doneThread;
                  if ( doneThread == threadCount )
                     Monitor.Pulse( hashSet );
               }
            } ).Start();
         }

         battleTechResourceLocator = null;
         HMACSHA256 hmacsha = new HMACSHA256( SecretKey );
         List<byte[]> hashList = new List<byte[]>();
         lock( hashSet ) {
            if ( doneThread != threadCount )
               Monitor.Wait( hashSet );
         }
         for ( int i = 0 ; i < manifestCounter ; i++ ) hashList.Add( hashSet[i] );
         __result = Convert.ToBase64String( hmacsha.ComputeHash( hashList.SelectMany( ( byte[] x ) => x ).ToArray<byte>() ) );
         Verbo( "Hash = {0}", __result );
         return false;
      }                 catch ( Exception ex ) { return Error( ex ); } }

      // ============ CSVReader ============

      // Compiled and shared regex
      private static Regex regex = new Regex("((?<=\\\")[^\\\"]*(?=\\\"(,|$)+)|(?<=,|^)[^,\\\"]*(?=,|$))", RegexOptions.Multiline | RegexOptions.Compiled );

      public static bool Override_CSVReader_ReadRow ( ref List<string> __result, ref int ___activeIdx, ref string[] ___rows ) {
         if ( ___activeIdx < 0 || ___activeIdx >= ___rows.Length ) {
            __result = null;
            return false;
         }
         __result = new List<string>();
         foreach ( object match in regex.Matches( ___rows[ ___activeIdx++ ] ) )
            __result.Add( Unescape( match.ToString() ) );
         return false;
      }
   }
}