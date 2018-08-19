using BattleTech;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Sheepy.BattleTechMod.Turbine {
   using static Mod;
   using static System.Reflection.BindingFlags;

   public class SafeGuards : BattleModModule {

      // Block processing of empty and repeated DataManagerRequestCompleteMessages
      private const bool FilterNullAndRepeatedMessage = true;

      // Cache some game properties to work around NPE of unknown cause.
      private const bool CacheVFXNames = true;
      private const bool CacheCombatConst = true;

      public override void ModStarts () {
         Verbo( "Some simple filters and safety shield first." );
         // A pretty safe filter that disables invalid or immediately duplicating complete messages.
         if ( FilterNullAndRepeatedMessage )
            Patch( typeof( DataManagerRequestCompleteMessage ).GetConstructors()[0], null, "Skip_DuplicateRequestCompleteMessage" );
         // Fix VFXNames.AllNames NPE
         if ( CacheVFXNames )
            Patch( typeof( VFXNamesDef ), "get_AllNames", "Override_VFX_get_AllNames", "Cache_VFX_get_AllNames" );
         if ( CacheCombatConst ) {
            // CombatGameConstants can be loaded and reloaded many times.  Cache it for reuse and fix an NPE.
            Patch( typeof( CombatGameConstants ), "LoadFromManifest", NonPublic, "Override_CombatGameConstants_LoadFromManifest", null );
            Patch( typeof( CombatGameConstants ), "OnDataLoaded", NonPublic, "Save_CombatGameConstants_Data", null );
         }
      }

      private static string lastMessage;

      public static void Skip_DuplicateRequestCompleteMessage ( DataManagerRequestCompleteMessage __instance ) {
         if ( String.IsNullOrEmpty( __instance.ResourceId ) ) {
            __instance.hasBeenPublished = true; // Skip publishing empty id
            return;
         }
         string key = GetKey( __instance.ResourceType, __instance.ResourceId );
         if ( lastMessage == key ) {
            if ( DebugLog ) Trace( "Skipping successive DataManagerRequestCompleteMessage " + key );
            __instance.hasBeenPublished = true;
         }  else
            lastMessage = key;
      }

      
      public static byte[] CombatConstantJSON;
      private static MethodInfo LoadMoraleResources, LoadMaintenanceResources;

      public static bool Override_CombatGameConstants_LoadFromManifest ( CombatGameConstants __instance ) { try {
         if ( CombatConstantJSON == null ) return true;
         __instance.FromJSON( UnZipStr( CombatConstantJSON ) );
         LoadMoraleResources?.Invoke( __instance, null );
         LoadMaintenanceResources?.Invoke( __instance, null );
         return false;
      }                 catch ( Exception ex ) { return Error( ex ); } }

      public static void Save_CombatGameConstants_Data ( MessageCenterMessage message ) {
         if ( message is DataManagerRequestCompleteMessage<string> msg && msg.ResourceType == BattleTechResourceType.CombatGameConstants ) { try {
            string json = Regex.Replace( DataProcess.StripComments( msg.Resource ), @"(?<=\n)\s+", "" ); // 48K to 32K
            fastJSON.JSON.Parse( json );
            CombatConstantJSON = ZipStr( json ); // 32K to 8K
            LoadMoraleResources = typeof( CombatGameConstants ).GetMethod( "LoadMoraleResources", NonPublic | Instance );
            LoadMaintenanceResources = typeof( CombatGameConstants ).GetMethod( "LoadMaintenanceResources", NonPublic | Instance );
         } catch ( Exception ex ) {
            CombatConstantJSON = null;
            Warn( ex );
         } }
      }

      // https://stackoverflow.com/a/2118959/893578
      public static byte[] ZipStr ( String str ) {
         using ( MemoryStream output = new MemoryStream() ) {
            using ( DeflateStream gzip = new DeflateStream( output, CompressionMode.Compress ) ) {
               using ( StreamWriter writer = new StreamWriter( gzip, Encoding.UTF8 ) ) {
                  writer.Write( str );
               }
            }
            return output.ToArray();
         }
      }

      // https://stackoverflow.com/a/2118959/893578
      public static string UnZipStr ( byte[] input ) {
         using ( MemoryStream inputStream = new MemoryStream( input ) ) {
            using ( DeflateStream gzip = new DeflateStream( inputStream, CompressionMode.Decompress ) ) {
               using ( StreamReader reader = new StreamReader( gzip, Encoding.UTF8 ) ) {
                  return reader.ReadToEnd();
               }
            }
         }
      }

      private static VFXNameDef[] nameCache;

      public static bool Override_VFX_get_AllNames ( ref VFXNameDef[] __result ) {
         if ( nameCache != null ) {
            __result = nameCache;
            return false;
         }
         // Will throw NPE if this.persistentDamage or this.persistentCrit is null.
         // No code change them, and NPE is reported to happens without Turbine.
         VFXNamesDef? def = BattleTechGame?.Combat?.Constants?.VFXNames;
         if ( def != null ) try {
            VFXNamesDef check = def.GetValueOrDefault();
            if ( check.persistentCrit == null || check.persistentDamage == null ) {
               Warn( "VFXNamesDef.persistentCrit and/or VFXNamesDef.persistentDamage is null on first load, using hardcoded list." );
               check.persistentDamage = "SmokeLrg_loop,SmokeSm_loop,ElectricalSm_loop,ElectricalLrg_loop,Sparks,ElectricalFailure_loop"
                  .Split(',').Select( e => new VFXNameDef(){ name = $"vfxPrfPrtl_mechDmg{e}" } ).ToArray();
               check.persistentCrit = "FireLrg_loop,FireSm_loop,SmokeSpark_loop"
                  .Split(',').Select( e => new VFXNameDef(){ name = $"vfxPrfPrtl_mechDmg{e}" } ).ToArray();
               typeof( CombatGameConstants ).GetProperty( "VFXNames" ).SetValue( BattleTechGame.Combat.Constants, check, null );
            }
         } catch ( Exception ex ) { Error( ex ); }
         return true;
      }

      public static void Cache_VFX_get_AllNames ( VFXNameDef[] __result ) {
         if ( ! ReferenceEquals( __result, nameCache ) ) {
            Info( "Caching VFXNamesDef.AllNames ({0})", __result.Length );
            nameCache = __result;
         }
      }
   }
}