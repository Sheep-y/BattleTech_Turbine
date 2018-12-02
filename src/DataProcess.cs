using BattleTech;
using BattleTech.Data;
using Harmony;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Sheepy.BattleTechMod.Turbine {
   using static Mod;

   public class DataProcess : BattleModModule {

      // Replace HBS's regex comment parser with a manually coded high performance parser
      private const bool StripJSON = true;

      // Calculate data hash in multi-thread
      private const bool MultiThreadHash = true;

      // Optimise CSVReader.ReadRow
      private const bool OptimiseCsvReadRow = true;

      //public static void LogStart() { Info( "Start" ); }
      //public static void LogEnd() { Info( "End" ); }

      public override void ModStarts () {
         if ( StripJSON )
            Patch( typeof( HBS.Util.JSONSerializationUtility ), "StripHBSCommentsFromJSON", "Override_StripComments", null );
         if ( MultiThreadHash )
            Patch( typeof( DataManager ), "GetDataHash", "MultiThreadDataHash", null );
         if ( OptimiseCsvReadRow ) {
            csvField = new Regex( "((?<=\\\")(?>[^\\\"]*)(?=\\\"(,|$)+)|(?<=,|^)(?>[^,\\\"]*)(?=,|$))", RegexOptions.Multiline | RegexOptions.Compiled );
            Patch( typeof( CSVReader ), "ReadRow", new Type[]{}, "Override_CSVReader_ReadRow", null );
         }
      }

      public override void GameStarts () {
         // Patch with authorization - https://github.com/Sheep-y/BattleTech_Turbine/issues/8
         if ( BattleMod.FoundMod( "BattletechPerformanceFix.Control" ) ) {
            Type DontStripComments = AppDomain.CurrentDomain.GetAssemblies().Select( e => e.GetType( "BattletechPerformanceFix.DontStripComments" ) ).FirstOrDefault( e => e != null );
            if ( DontStripComments != null )
               Patch( DontStripComments, "HBSStripCommentsMirror", "Override_StripComments", null );
         }
      }

      public static string Unescape ( string value ) {
         if ( value.StartsWith( "\"" ) && value.EndsWith( "\"" ) ) {
            value = value.Substring( 1, value.Length - 2 );
            if ( value.Contains( "\"\"" ) ) value = value.Replace( "\"\"", "\"" );
         }
         return value;
      }

      // ============ Json Process ============

      private static bool commentDetected;

      [ HarmonyPriority( Priority.LowerThanNormal ) ]
      public static bool Override_StripComments ( ref string __result, string json ) { try {
         commentDetected = false;
         __result = StripComments( json );
         if ( commentDetected ) // Try parse stripped result to make sure it is good
            fastJSON.JSON.Parse( __result );
         return false;
      }  catch ( Exception ex ) {
         return Error( ex );
      } }

      public static string StripComments ( string json ) {
         if ( json == null ) return null;
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

      private static bool Match ( string json, int pos, String txt ) {
         if ( json.Length <= pos + txt.Length ) return false;
         string sub = json.Substring( pos, txt.Length );
         return sub == txt;
      }
      private static bool SkipWS ( StringBuilder buf, string json, ref int pos, int skipStart, int headEnd, string until ) {
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
      private static bool Skip ( StringBuilder buf, string json, ref int pos, int skipStart, int headEnd, string until ) {
         if ( json.Length <= headEnd ) return false;
         int tailStart = json.IndexOf( until, headEnd );
         if ( tailStart < 0 ) return false;
         if ( skipStart > 0 )
            buf.Append( json.Substring( pos, skipStart - pos ) );
         pos = tailStart + until.Length;
         commentDetected = true;
         return true;
      }
      private static char Peek ( String json, int pos ) {
         if ( json.Length <= pos ) return '\u0000';
         return json[ pos ];
      }

      // ============ Data Hash ============

      private static byte[] SecretKey;
      private const int JobPerLoop = 16, HashSize = 32;

      public static bool MultiThreadDataHash ( ref string __result, byte[] ___secret_key, params BattleTechResourceType[] typesToHash ) { try {
         if ( DebugLog ) Verbo( "Prepare to get data hash." );
         SecretKey = ___secret_key;
         if ( SecretKey == null ) throw new NullReferenceException( "DataManager.secret_key is null" );

         int manifestCounter = 0, pos = 0;
         // For me, over half the pre-Turbine time is spent on this new BattleTechResourceLocator.  Post-Turbine it consume most time!
         BattleTechResourceLocator battleTechResourceLocator = new BattleTechResourceLocator(); 
         Dictionary<int,VersionManifestEntry> manifestMap = new Dictionary<int,VersionManifestEntry>( 4000 ); // Vanilla has 900+. Mods may adds a lot more.
         foreach ( BattleTechResourceType type in typesToHash )
            foreach ( VersionManifestEntry versionManifestEntry in battleTechResourceLocator.AllEntriesOfResource( type ) )
               manifestMap.Add( manifestCounter++, versionManifestEntry );
         battleTechResourceLocator = null;

         Dictionary<int,byte[]> hashMap = new Dictionary<int,byte[]>();
         RunHashs( manifestMap, hashMap );

         byte[] allHash = new byte[ HashSize * hashMap.Count ];
         for ( int i = 0 ; i < manifestCounter ; i++ ) {
            if ( ! hashMap.TryGetValue( i, out byte[] hash ) ) continue;
            Buffer.BlockCopy( hash, 0, allHash, pos, HashSize );
            pos += HashSize;
         }
         __result = Convert.ToBase64String( new HMACSHA256( SecretKey ).ComputeHash( allHash ) );
         if ( DebugLog ) Verbo( "Hash = {0}", __result );
         return false;
      }                 catch ( Exception ex ) { return Error( ex ); } }

      private static void RunHashs ( Dictionary<int,VersionManifestEntry> manifestList, Dictionary<int,byte[]> hashSet ) {
         int workingThread = Math.Max( 2, Math.Min( Environment.ProcessorCount*2, 32 ) );
         Info( "Calculating data hash with {0} threads.", workingThread );
         for ( int i = 0 ; i < workingThread ; i++ ) {
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
                  if ( --workingThread <= 0 )
                     Monitor.Pulse( hashSet );
               }
            } ).Start();
         }
         lock( hashSet ) {
            if ( workingThread > 0 )
               Monitor.Wait( hashSet );
         }
      }

      // ============ CSVReader ============

      // Compiled and shared regex
      private static Regex csvField;

      public static bool Override_CSVReader_ReadRow ( ref List<string> __result, ref int ___activeIdx, ref string[] ___rows ) {
         if ( ___activeIdx < 0 || ___activeIdx >= ___rows.Length ) {
            __result = null;
            return false;
         }
         string row = ___rows[ ___activeIdx++ ];
         if ( row.Contains( '"' ) ) {
            __result = new List<string>( 11 );
            foreach ( object match in csvField.Matches( row ) )
               __result.Add( Unescape( match.ToString() ) );
         } else
            __result = row.Split( ',' ).ToList();

         return false;
      }
   }
}