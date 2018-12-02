using BattleTech;
using BattleTech.Data;
using Sheepy.Logging;
using System;
using System.Diagnostics;
using System.Collections.Generic;

namespace Sheepy.BattleTechMod.Turbine {

   public class Mod : BattleMod {

      // A kill switch to press when any things go wrong during initialisation.
      // Default true and set to false after initial patch success.  Also set to true after any exception.
      private static bool UnpatchManager = true;

      // Performance hit varies by machine spec.
      internal const bool DebugLog = false;

      public static void Init () {
         new Mod().Start( ref ModLog );
      }

      public override void ModStarts () {
         Log.LogLevel = SourceLevels.Verbose;
         Add( new SafeGuards() );

         Verbo( "Applying Turbine." );
         Type dmType = typeof( DataManager );
         logger = HBS.Logging.Logger.GetLogger( "Data.DataManager" );
         Patch( dmType, "ProcessRequests", nameof( Override_ProcessRequests ), null );
         Patch( dmType, "RequestResource_Internal", nameof( Prefix_RequestResource_Internal ), null );
         UnpatchManager = false;
         Info( "Turbine initialised" );

         Add( new DataProcess() );

         if ( DebugLog ) Log.LogLevel = SourceLevels.Verbose | SourceLevels.ActivityTracing;
      }

      public override void GameStartsOnce () {
         if ( UnpatchManager ) return;
         Info( "Mods found: " + BattleMod.GetModList().Concat() );
      }

      // ============ Compressor & Turbine - the main rewrite ============

      // Cache or access to original manager states
      private static HBS.Logging.ILog logger;

      public static bool Override_ProcessRequests ( DataManager __instance, List<DataManager.DataManagerLoadRequest> ___foregroundRequestsList, uint ___foregroundRequestsCurrentAllowedWeight ) { try {
         if ( UnpatchManager ) return true;
			for ( int i = 0, len = ___foregroundRequestsList.Count ; i < len ; i++ ) {
				DataManager.DataManagerLoadRequest request = ___foregroundRequestsList[ i ];
				if ( request.State != DataManager.DataManagerLoadRequest.RequestState.Requested ) continue;
				request.RequestWeight.SetAllowedWeight( ___foregroundRequestsCurrentAllowedWeight );
				if ( request.IsMemoryRequest )
					__instance.RemoveObjectOfType( request.ResourceId, request.ResourceType );
				if ( ! request.ManifestEntryValid )
					LogManifestEntryValid( request );
				else if (!request.RequestWeight.RequestAllowed)
					request.NotifyLoadComplete();
				else {
               if ( DebugLog ) Trace( "Loading {0} {1}", request.ResourceType, request.ResourceId );
					request.Load();
            }
			}
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( ex ); } }

      private static void LogManifestEntryValid ( DataManager.DataManagerLoadRequest request ) {
         logger.LogError( string.Format("LoadRequest for {0} of type {1} has an invalid manifest entry. Any requests for this object will fail.", request.ResourceId, request.ResourceType ) );
         request.NotifyLoadFailed();
      }

      private static BattleTechResourceType lastResourceType;
      private static string lastIdentifier;

      public static bool Prefix_RequestResource_Internal ( DataManager __instance, BattleTechResourceType resourceType, string identifier ) {
         if ( string.IsNullOrEmpty( identifier ) || ( identifier == lastIdentifier && resourceType == lastResourceType ) ) {
            if ( DebugLog ) Verbo( "Skipping empty or dup resource {0} {1}", resourceType, identifier );
            return false;
         }
         lastResourceType = resourceType;
         lastIdentifier = identifier;
         return true;
      }

      // ============ Safety System: Kill Switch and Logging ============

      private static bool KillManagerPatch ( Exception err ) { try {
         Error( err );
         Info( "Suicide due to exception." );
         UnpatchManager = true;
         return true;
      } catch ( Exception ex ) {
         return Error( ex );
      } }

      internal static Logger ModLog = BattleMod.BT_LOG;

      public static void Trace ( object message = null, params object[] args ) { ModLog.Trace( message, args ); }
      public static void Verbo ( object message = null, params object[] args ) { ModLog.Verbo( message, args ); }
      public static void Info  ( object message = null, params object[] args ) { ModLog.Info ( message, args ); }
      public static void Warn  ( object message = null, params object[] args ) { ModLog.Warn ( message, args ); }
      public static bool Error ( object message = null, params object[] args ) { ModLog.Error( message, args ); return true; }
   }
}