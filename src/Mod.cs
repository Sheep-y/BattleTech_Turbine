using BattleTech;
using BattleTech.Data;
using Sheepy.Logging;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Sheepy.BattleTechMod.Turbine {
   using static System.Reflection.BindingFlags;

   public class Mod : BattleMod {

      // A kill switch to press when any things go wrong during initialisation.
      // Default true and set to false after initial patch success.  Also set to true after any exception.
      private static bool UnpatchManager = true;

      // Maintain a separate loading queue from task queue.
      private const bool LoadingQueue = true;

      // Don't timeout my load!  Set to false!
      private const bool EnableTimeout = false;

      // Check load weights during process requests?  Set to false to skip the checks!
      private const bool EnableLoadCheck = false;

      // Hack MechDef/MechComponentDef dependency checking?
      private const bool OverrideMechDefDependencyCheck = true;
      private const bool OverrideMechCompDependencyCheck = false;

      // Performance hit varies by machine spec.
      internal const bool DebugLog = false;

      public static void Init () {
         new Mod().Start( ref ModLog );
      }

      private static Type dmType;

      static void Main () {
         string input = File.ReadAllText( "data.json" );
         Console.Write( DataProcess.StripComments( input ) );
         Console.ReadKey();
      }

#pragma warning disable CS0162 // Disable warning of unreachable code due to DebugLog
      public override void ModStarts () {
         Info( "Loading queue {0}.", LoadingQueue  ? "on" : "off" );
         Info( "Timeout {0}.", EnableTimeout  ? "off" : "on" );
         Info( "OverrideMechDefDependencyCheck {0}.", OverrideMechDefDependencyCheck  ? "on" : "off" );
         Info( "OverrideMechCompDependencyCheck {0}.", OverrideMechCompDependencyCheck  ? "on" : "off" );
         Log.LogLevel = SourceLevels.Verbose;
         stopwatch = new Stopwatch();
         Add( new SafeGuards() );

         Verbo( "Ok let's try to install real Turbine." );
         dmType = typeof( DataManager );
         prewarmRequests = dmType.GetField( "prewarmRequests", NonPublic | Instance );
         isLoadingAsync = dmType.GetField( "isLoadingAsync", NonPublic | Instance );
         CreateByResourceType = dmType.GetMethod( "CreateByResourceType", NonPublic | Instance );
         SaveCache = dmType.GetMethod( "SaveCache", NonPublic | Instance );
         if ( prewarmRequests == null || isLoadingAsync == null || CreateByResourceType == null || SaveCache == null )
            throw new NullReferenceException( "One or more DataManager fields not found with reflection." );
         logger = HBS.Logging.Logger.GetLogger( "Data.DataManager" );
         Patch( dmType.GetConstructors()[0], "DataManager_ctor", null );
         Patch( dmType, "Clear", "Override_Clear", null );
         Patch( dmType, "CheckAsyncRequestsComplete", "Override_CheckRequestsComplete", null );
         Patch( dmType, "CheckRequestsComplete", "Override_CheckRequestsComplete", null );
         Patch( dmType, "GraduateBackgroundRequest", "Override_GraduateBackgroundRequest", null );
         Patch( dmType, "NotifyFileLoaded", "Override_NotifyFileLoaded", null );
         Patch( dmType, "NotifyFileLoadedAsync", "Override_NotifyFileLoadedAsync", null );
         Patch( dmType, "NotifyFileLoadFailed", "Override_NotifyFileLoadFailed", null );
         Patch( dmType, "ProcessAsyncRequests", "Override_ProcessAsyncRequests", null );
         Patch( dmType, "ProcessRequests", "Override_ProcessRequests", null );
         Patch( dmType, "RequestResourceAsync_Internal", "Override_RequestResourceAsync_Internal", null );
         Patch( dmType, "RequestResource_Internal", "Override_RequestResource_Internal", null );
         Patch( dmType, "SetLoadRequestWeights", "Override_SetLoadRequestWeights", null );
         Patch( dmType, "UpdateRequestsTimeout", "Override_UpdateRequestsTimeout", null );
         foreground = new Dictionary<string, DataManager.DataManagerLoadRequest>(4096);
         background = new Dictionary<string, DataManager.DataManagerLoadRequest>(4096);
         if ( LoadingQueue )
            foregroundLoading = new HashSet<DataManager.DataManagerLoadRequest>();
         depender = new Dictionary<object, HashSet<string>>();
         dependee = new Dictionary<string, HashSet<object>>();
         if ( OverrideMechDefDependencyCheck ) {
            Patch( typeof( MechDef ), "CheckDependenciesAfterLoad", "Skip_CheckMechDependenciesAfterLoad", "Cleanup_CheckMechDependenciesAfterLoad" );
            Patch( typeof( MechDef ), "RequestDependencies", "StartLogMechDefDependencies", "StopLogMechDefDependencies" );
         }
         if ( OverrideMechCompDependencyCheck ) {
            LoadedComp = new HashSet<MechComponentDef>();
            Patch( typeof( MechComponentDef ), "DependenciesLoaded", null, "Record_CompDependenciesLoaded" );
            Patch( typeof( MechComponentDef ), "CheckDependenciesAfterLoad", "Skip_CheckCompDependenciesAfterLoad", "Cleanup_CheckCompDependenciesAfterLoad" );
         }
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

      // Manager states that were taken over
      private static Dictionary<string, DataManager.DataManagerLoadRequest> foreground, background;
      private static HashSet<DataManager.DataManagerLoadRequest> foregroundLoading;
      private static float currentTimeout = -1, currentAsyncTimeout = -1;
      private static ICollection<DataManager.DataManagerLoadRequest> queue { get { if ( LoadingQueue ) return foregroundLoading; return foreground.Values; } }

      // Cache or access to original manager states
      private static DataManager manager;
      private static MessageCenter center;
      private static HBS.Logging.ILog logger;
      private static FieldInfo prewarmRequests, isLoadingAsync;
      private static MethodInfo CreateByResourceType, SaveCache;

      // Track queue time
      private static Stopwatch stopwatch;
      private static long totalLoadTime = 0;

      private static string GetName ( object obj ) { 
         if ( obj is MechDef mech ) return mech.Name + " (" + mech.ChassisID + ")";
         if ( obj is MechComponentDef comp ) return comp.Description.Manufacturer + " " + comp.Description.Name;
         return obj?.ToString() ?? null;
      }
      internal static string GetKey ( DataManager.DataManagerLoadRequest request ) { return GetKey( request.ResourceType, request.ResourceId ); }
      internal static string GetKey ( BattleTechResourceType resourceType, string id ) { return (int) resourceType + "_" + id; }

      public static void DataManager_ctor ( MessageCenter messageCenter ) {
         center = messageCenter;
         if ( DebugLog ) Info( "DataManager created." );
      }

      public static void Override_Clear ( DataManager __instance ) {
         if ( UnpatchManager ) return;
         foreground.Clear();
         background.Clear();
         if ( LoadingQueue )
            foregroundLoading.Clear();
         depender.Clear();
         dependee.Clear();
         if ( DebugLog ) Info( "All queues cleared." );
         manager = __instance;
      }

      public static bool Override_CheckRequestsComplete ( ref bool __result ) {
         if ( UnpatchManager ) return true;
         __result = CheckRequestsComplete();
         return false;
      }
      public static bool Override_CheckAsyncRequestsComplete ( ref bool __result ) {
         if ( UnpatchManager ) return true;
         __result = CheckAsyncRequestsComplete();
         return false;
      }
      private static bool CheckRequestsComplete () {
         bool done = queue.All( IsComplete );
         if ( DebugLog && LoadingQueue && done && foregroundLoading.Count > 0 ) {
            Warn( $"Found {foregroundLoading.Count} unnotified completed requests in loading queue:" );
            foreach ( var request in foregroundLoading ) Info( "   {0} ({1})", GetKey( request ), request.GetType() );
         }
         return done;
      }
      private static bool CheckAsyncRequestsComplete () { return background.Values.All( IsComplete ); }
      private static bool IsComplete ( DataManager.DataManagerLoadRequest e ) { return e.IsComplete(); }

      public static bool Override_GraduateBackgroundRequest ( DataManager __instance, ref bool __result, BattleTechResourceType resourceType, string id ) { try {
         if ( UnpatchManager ) return true;
         __result = GraduateBackgroundRequest( __instance, resourceType, id );
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      private static bool GraduateBackgroundRequest ( DataManager me, BattleTechResourceType resourceType, string id ) {
         string key = GetKey( resourceType, id );
         if ( ! background.TryGetValue( key, out DataManager.DataManagerLoadRequest dataManagerLoadRequest ) )
            return false;
         if ( DebugLog ) Info( "Graduating {0} ({1}) from background to foreground.", GetKey( dataManagerLoadRequest ), dataManagerLoadRequest.GetType() );
         dataManagerLoadRequest.SetAsync( false );
         dataManagerLoadRequest.ResetRequestState();
         background.Remove( key );
         foreground.Add( key, dataManagerLoadRequest );
         if ( LoadingQueue && ! dataManagerLoadRequest.IsComplete() )
            foregroundLoading.Add( dataManagerLoadRequest );
         bool wasLoadingAsync = (bool) isLoadingAsync.GetValue( me );
         bool nowLoadingAsync = ! CheckAsyncRequestsComplete();
         if ( nowLoadingAsync != wasLoadingAsync ) {
            isLoadingAsync.SetValue( me, nowLoadingAsync );
            if ( wasLoadingAsync ) {
               SaveCache.Invoke( me, null );
               background.Clear();
               center.PublishMessage( new DataManagerAsyncLoadCompleteMessage() );
            }
         }
         return true;
      }

      public static bool Override_NotifyFileLoaded ( DataManager __instance, DataManager.DataManagerLoadRequest request, ref bool ___isLoading ) { try {
         if ( UnpatchManager ) return true;
         NotifyFileLoaded( __instance, request, ref ___isLoading );
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      private static void NotifyFileLoaded ( DataManager me, DataManager.DataManagerLoadRequest request, ref bool isLoading ) {
         if ( request.Prewarm != null ) {
            if ( DebugLog ) Trace( "Notified Prewarm: {0}", GetKey( request ) );
            List<PrewarmRequest> pre = (List<PrewarmRequest>) prewarmRequests.GetValue( me );
            pre.Remove( request.Prewarm );
         }
         if ( DebugLog ) Trace( "Notified Done: {0}", GetKey( request ) );
         CheckMechDefDependencies( request );
         if ( LoadingQueue && request.IsComplete() )
            foregroundLoading.Remove( request );
         if ( CheckRequestsComplete() ) {
            if ( foreground.Count > 0 ) {
               stopwatch.Stop();
               Info( "Foreground queue ({0}) cleared. {1:n0}ms this queue, {2:n0}ms total.", foreground.Count, stopwatch.ElapsedMilliseconds, totalLoadTime += stopwatch.ElapsedMilliseconds );
               stopwatch.Reset();
            } else if ( DebugLog )
               Verbo( "Empty foreground queue cleared by {0}.", GetKey( request ) );
            isLoading = false;
            SaveCache.Invoke( me, null );
            foreground.Clear();
            if ( LoadingQueue )
               foregroundLoading.Clear();
            depender.Clear();
            dependee.Clear();
            center.PublishMessage( new DataManagerLoadCompleteMessage() );
         }
      }

      public static bool Override_NotifyFileLoadedAsync ( DataManager __instance, DataManager.DataManagerLoadRequest request ) { try {
         if ( UnpatchManager ) return true;
         NotifyFileLoadedAsync( __instance, request );
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      private static void NotifyFileLoadedAsync ( DataManager me, DataManager.DataManagerLoadRequest request ) {
         if ( request.Prewarm != null ) {
            if ( DebugLog ) Trace( "Notified Prewarm Async: " + GetKey( request ) );
            List<PrewarmRequest> pre = (List<PrewarmRequest>) prewarmRequests.GetValue( me );
            pre.Remove( request.Prewarm );
         }
         if ( DebugLog ) Trace( "Notified Done Async: " + GetKey( request ) );
         CheckMechDefDependencies( request );
         if ( CheckAsyncRequestsComplete() ) {
            if ( DebugLog ) Verbo( "Background queue cleared by {0}.", GetKey( request ) );
            isLoadingAsync.SetValue( me, false );
            SaveCache.Invoke( me, null );
            background.Clear();
            center.PublishMessage( new DataManagerAsyncLoadCompleteMessage() );
         }
      }

      public static bool Override_NotifyFileLoadFailed ( DataManager __instance, DataManager.DataManagerLoadRequest request, ref bool ___isLoading ) { try {
         if ( UnpatchManager ) return true;
         string key = GetKey( request );
         if ( DebugLog ) Trace ( "Notified Failed: {0}", key );
         if ( foreground.Remove( key ) )
            NotifyFileLoaded( __instance, request, ref ___isLoading );
         else if ( background.Remove( key ) )
            NotifyFileLoadedAsync( __instance, request );
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      private static void CheckMechDefDependencies ( DataManager.DataManagerLoadRequest request ) {
         string key = GetKey( request );
         if ( ! dependee.TryGetValue( key, out HashSet<object> dependents ) ) return;
         dependee.Remove( key );
         foreach ( object dependent in dependents ) {
            if ( depender.TryGetValue( dependent, out HashSet<string> list ) && list.Remove( key ) ) {
               if ( list.Count > 0 ) {
                  if ( DebugLog ) Trace( "Found MechDef dependency. Check {0} of {1}. {2} remains.", key, GetName( dependent ), list.Count );
                  continue;
               }
               if ( DebugLog ) Verbo( "All depencency loaded for {0}.\n{1}", GetName( dependent ) );
               checkingMech = null;
               if ( dependent is MechDef mech )
                  mech.CheckDependenciesAfterLoad( new DataManagerLoadCompleteMessage() );
            }
         }
      }

      public static bool Override_ProcessRequests ( DataManager __instance, uint ___foregroundRequestsCurrentAllowedWeight, ref bool ___isLoading ) { try {
         if ( UnpatchManager ) return true;
         if ( queue.Count <= 0 ) return false; // Early abort before reflection
         DataManager me = __instance;
         int lightLoad = 0, heavyLoad = 0;
         if ( DebugLog ) Trace( "Processing {0} foreground requests", queue.Count );
         foreach ( DataManager.DataManagerLoadRequest request in queue.ToArray() ) {
            if ( EnableLoadCheck )
               if ( lightLoad >= DataManager.MaxConcurrentLoadsLight && heavyLoad >= DataManager.MaxConcurrentLoadsHeavy )
                  break;
            request.RequestWeight.SetAllowedWeight( ___foregroundRequestsCurrentAllowedWeight );
            if ( request.State == DataManager.DataManagerLoadRequest.RequestState.Requested ) {
               if ( request.IsMemoryRequest )
                  me.RemoveObjectOfType( request.ResourceId, request.ResourceType );
               if ( request.AlreadyLoaded ) {
                  if ( ! request.DependenciesLoaded( ___foregroundRequestsCurrentAllowedWeight ) ) {
                     DataManager.ILoadDependencies dependencyLoader = request.TryGetDependencyLoader();
                     if ( dependencyLoader != null ) {
                        request.RequestWeight.SetAllowedWeight( ___foregroundRequestsCurrentAllowedWeight );
                        dependencyLoader.RequestDependencies( me, () => {
                           if ( dependencyLoader.DependenciesLoaded( ___foregroundRequestsCurrentAllowedWeight ) )
                              request.NotifyLoadComplete();
                        }, request );
                        if ( EnableLoadCheck ) {
                           if ( request.RequestWeight.RequestWeight == 10u ) {
                              if ( DataManager.MaxConcurrentLoadsLight > 0 )
                                 lightLoad++;
                           } else if ( DataManager.MaxConcurrentLoadsHeavy > 0 )
                              heavyLoad++;
                        }
                        ___isLoading = true;
                        if ( EnableTimeout ) me.ResetRequestsTimeout();
                     }
                  } else
                     request.NotifyLoadComplete();
               } else {
                  if ( EnableLoadCheck )
                     if ( lightLoad >= DataManager.MaxConcurrentLoadsLight && heavyLoad >= DataManager.MaxConcurrentLoadsHeavy )
                        break;
                  if ( ! request.ManifestEntryValid ) {
                     logger.LogError( string.Format( "LoadRequest for {0} of type {1} has an invalid manifest entry. Any requests for this object will fail.", request.ResourceId, request.ResourceType ) );
                     request.NotifyLoadFailed();
                  } else if ( ! request.RequestWeight.RequestAllowed ) {
                     request.NotifyLoadComplete();
                  } else {
                     if ( EnableLoadCheck ) {
                        if ( request.RequestWeight.RequestWeight == 10u ) {
                           if ( DataManager.MaxConcurrentLoadsLight > 0 )
                              lightLoad++;
                        } else if ( DataManager.MaxConcurrentLoadsHeavy > 0 )
                           heavyLoad++;
                     }
                     ___isLoading = true;
                     if ( DebugLog ) Verbo( "Loading {0}.", GetKey( request ) );
                     request.Load();
                     if ( EnableTimeout ) me.ResetRequestsTimeout();
                  }
               }
            }
         }
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      public static bool Override_ProcessAsyncRequests ( DataManager __instance, uint ___backgroundRequestsCurrentAllowedWeight ) {
         if ( UnpatchManager ) return true;
         if ( background.Count < 0 ) return false; // Early abort before reflection
         DataManager me = __instance;
         if ( DebugLog ) Trace( "Processing {0} background requests", background.Count );
         foreach ( DataManager.DataManagerLoadRequest request in background.Values ) {
            request.RequestWeight.SetAllowedWeight( ___backgroundRequestsCurrentAllowedWeight );
            DataManager.DataManagerLoadRequest.RequestState state = request.State;
            if ( state == DataManager.DataManagerLoadRequest.RequestState.Processing ) return false;
            if ( state == DataManager.DataManagerLoadRequest.RequestState.RequestedAsync ) {
               if ( request.IsMemoryRequest )
                  me.RemoveObjectOfType( request.ResourceId, request.ResourceType );
               if ( request.AlreadyLoaded ) {
                  if ( !request.DependenciesLoaded( ___backgroundRequestsCurrentAllowedWeight ) ) {
                     DataManager.ILoadDependencies dependencyLoader = request.TryGetDependencyLoader();
                     if ( dependencyLoader != null ) {
                        request.RequestWeight.SetAllowedWeight( ___backgroundRequestsCurrentAllowedWeight );
                        dependencyLoader.RequestDependencies( me, () => {
                           if ( dependencyLoader.DependenciesLoaded( request.RequestWeight.AllowedWeight ) )
                              request.NotifyLoadComplete();
                        }, request );
                        isLoadingAsync.SetValue( me, true );
                        if ( EnableTimeout ) me.ResetAsyncRequestsTimeout();
                     }
                  } else
                     request.NotifyLoadComplete();
               } else if ( !request.ManifestEntryValid ) {
                  logger.LogError( string.Format( "LoadRequest for {0} of type {1} has an invalid manifest entry. Any requests for this object will fail.", request.ResourceId, request.ResourceType ) );
                  request.NotifyLoadFailed();
               } else if ( !request.RequestWeight.RequestAllowed ) {
                  request.NotifyLoadComplete();
               } else {
                  isLoadingAsync.SetValue( me, true );
                  if ( DebugLog ) Verbo( "Loading Async {0}.", GetKey( request ) );
                  request.Load();
                  if ( EnableTimeout ) me.ResetAsyncRequestsTimeout();
               }
               return false;
            }
         }
         return false;
      }

      public static bool Override_RequestResourceAsync_Internal ( DataManager __instance, BattleTechResourceType resourceType, string identifier, PrewarmRequest prewarm, uint ___backgroundRequestsCurrentAllowedWeight ) { try {
         if ( UnpatchManager || string.IsNullOrEmpty( identifier ) ) return false;
         DataManager me = __instance;
         string key = GetKey( resourceType, identifier );
         background.TryGetValue( key, out DataManager.DataManagerLoadRequest dataManagerLoadRequest );
         if ( dataManagerLoadRequest != null ) {
            if ( dataManagerLoadRequest.State == DataManager.DataManagerLoadRequest.RequestState.Complete ) {
               if ( !dataManagerLoadRequest.DependenciesLoaded( ___backgroundRequestsCurrentAllowedWeight ) ) {
                  dataManagerLoadRequest.ResetRequestState();
               } else {
                  dataManagerLoadRequest.NotifyLoadComplete();
               }
            } else {
               if ( DebugLog ) Warn( "Cannot move {0} to top of background queue.", GetKey( dataManagerLoadRequest ) );
               // Move to top of queue. Not supported by HashTable.
               //backgroundRequest.Remove( dataManagerLoadRequest );
               //backgroundRequest.Insert( 0, dataManagerLoadRequest );
            }
            return false;
         }
         bool isForeground = foreground.ContainsKey( key );
         bool isTemplate = identifier.ToLowerInvariant().Contains("template");
         if ( ! isForeground && ! isTemplate ) {
            dataManagerLoadRequest = (DataManager.DataManagerLoadRequest) CreateByResourceType.Invoke( me, new object[]{ resourceType, identifier, prewarm } );
            dataManagerLoadRequest.SetAsync( true );
            if ( DebugLog ) Info( "Queued Async: {0} ({1}) ", key, dataManagerLoadRequest.GetType() );
            background.Add( key, dataManagerLoadRequest );
         }
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      private static BattleTechResourceType lastResourceType;
      private static string lastIdentifier;

      public static bool Override_RequestResource_Internal ( DataManager __instance, BattleTechResourceType resourceType, string identifier, PrewarmRequest prewarm, bool allowRequestStacking, ref bool ___isLoading ) { try {
         if ( UnpatchManager || string.IsNullOrEmpty( identifier ) ) return false;
         // Quickly skip duplicate request
         if ( resourceType == lastResourceType && identifier == lastIdentifier ) return false;
         lastResourceType = resourceType;
         lastIdentifier = identifier;
         DataManager me = __instance;
         string key = GetKey( resourceType, identifier );
         if ( monitoringMech != null ) LogDependee( monitoringMech, key );
         foreground.TryGetValue( key, out DataManager.DataManagerLoadRequest dataManagerLoadRequest );
         if ( dataManagerLoadRequest != null ) {
            if ( dataManagerLoadRequest.State != DataManager.DataManagerLoadRequest.RequestState.Complete || !dataManagerLoadRequest.DependenciesLoaded( dataManagerLoadRequest.RequestWeight.RequestWeight ) ) {
               if ( allowRequestStacking )
                  dataManagerLoadRequest.IncrementCacheCount();
            } else
               NotifyFileLoaded( me, dataManagerLoadRequest, ref ___isLoading );
            return false;
         }
         bool movedToForeground = GraduateBackgroundRequest( me, resourceType, identifier);
         bool skipLoad = false;
         bool isTemplate = identifier.ToLowerInvariant().Contains("template");
         if ( !movedToForeground && !skipLoad && !isTemplate ) {
            dataManagerLoadRequest = (DataManager.DataManagerLoadRequest) CreateByResourceType.Invoke( me, new object[]{ resourceType, identifier, prewarm } );
            if ( foreground.Count <= 0 ) {
               Info( "Starting new queue" );
               stopwatch.Start();
            }
            if ( DebugLog ) Verbo( "Queued: {0} ({1})", key, dataManagerLoadRequest.GetType() );
            //if ( key == "19_CombatGameConstants" ) Info( Logger.Stacktrace );
            foreground.Add( key, dataManagerLoadRequest );
            if ( LoadingQueue && ! dataManagerLoadRequest.IsComplete() )
               foregroundLoading.Add( dataManagerLoadRequest );
         }
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      private static void LogDependee ( object monitored, string key ) {
         if ( ! dependee.TryGetValue( key, out HashSet<object> depList ) )
            dependee[ key ] = depList = new HashSet<object>();
         if ( ! depList.Contains( monitored ) ) {
            if ( DebugLog ) Verbo( "   " + GetName( monitored ) + " requested " + key );
            depList.Add( monitored );
            if ( depender.TryGetValue( monitored, out HashSet<string> dependList ) )
               dependList.Add( key );
         }
      }

      public static bool Override_SetLoadRequestWeights ( DataManager __instance, uint foregroundRequestWeight, uint backgroundRequestWeight, ref uint ___foregroundRequestsCurrentAllowedWeight, ref uint ___backgroundRequestsCurrentAllowedWeight ) { try {
         if ( UnpatchManager ) return true;
         if ( DebugLog ) Info( "Set LoadRequestWeights {0}/{1} on {2}/{3} loading foreground/background requests.", foregroundRequestWeight, backgroundRequestWeight, foregroundLoading.Count, background.Count );
         ___foregroundRequestsCurrentAllowedWeight = foregroundRequestWeight;
         ___backgroundRequestsCurrentAllowedWeight = backgroundRequestWeight;
         foreach ( DataManager.DataManagerLoadRequest dataManagerLoadRequest in queue )
            if ( foregroundRequestWeight > dataManagerLoadRequest.RequestWeight.AllowedWeight )
               dataManagerLoadRequest.RequestWeight.SetAllowedWeight( foregroundRequestWeight );
         foreach ( DataManager.DataManagerLoadRequest dataManagerLoadRequest in background.Values )
            if ( backgroundRequestWeight > dataManagerLoadRequest.RequestWeight.AllowedWeight )
               dataManagerLoadRequest.RequestWeight.SetAllowedWeight( backgroundRequestWeight );
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      public static bool Override_UpdateRequestsTimeout ( DataManager __instance, float deltaTime ) { try {
         if ( UnpatchManager ) return true;
         if ( ! EnableTimeout ) return false;
         DataManager me = __instance;
         if ( currentTimeout >= 0f ) {
            if ( queue.Any( IsProcessing ) ) {
               if ( DebugLog ) Warn( "Foreground request timeout." );
               DataManager.DataManagerLoadRequest[] list = queue.Where( IsProcessing ).ToArray();
               currentTimeout += deltaTime;
               if ( currentTimeout > list.Count() * 0.2f ) {
                  foreach ( DataManager.DataManagerLoadRequest dataManagerLoadRequest in list ) {
                     logger.LogWarning( string.Format( "DataManager Request for {0} has taken too long. Cancelling request. Your load will probably fail", dataManagerLoadRequest.ResourceId ) );
                     dataManagerLoadRequest.NotifyLoadFailed();
                  }
                  currentTimeout = -1f;
               }
            }
         }
         if ( currentAsyncTimeout >= 0f && background.Count > 0 ) {
            currentAsyncTimeout += deltaTime;
            if ( currentAsyncTimeout > 20f ) {
               if ( DebugLog ) Warn( "Background request timeout." );
               DataManager.DataManagerLoadRequest dataManagerLoadRequest = background.Values.First( IsProcessing );
               if ( dataManagerLoadRequest != null ) {
                  logger.LogWarning( string.Format( "DataManager Async Request for {0} has taken too long. Cancelling request. Your load will probably fail", dataManagerLoadRequest.ResourceId ) );
                  dataManagerLoadRequest.NotifyLoadFailed();
               }
               currentAsyncTimeout = -1f;
            }
         }
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      private static bool IsProcessing ( DataManager.DataManagerLoadRequest e ) {
         return e.State == DataManager.DataManagerLoadRequest.RequestState.Processing;
      }

      // ============ MechDef Bypass ============

      private static Dictionary<object, HashSet<string>> depender;
      private static Dictionary<string, HashSet<object>> dependee;
      private static MechDef monitoringMech, checkingMech;

      public static bool Skip_CheckMechDependenciesAfterLoad ( MechDef __instance, MessageCenterMessage message ) { try {
         if ( UnpatchManager ) return true;
         MechDef me = __instance;
         if ( ! depender.TryGetValue( me, out HashSet<string> toLoad ) ) {
            if ( checkingMech == null ) {
               if ( DebugLog ) Verbo( "Allowing MechDef verify {0}.", GetName( me ) );
               checkingMech = __instance;
               return true;
            }
            if ( DebugLog ) Trace( "Bypassing MechDef check {0} because checking {1}.", GetName( me ), GetName( checkingMech ) );
            return false;
         }
         if ( toLoad.Count > 0 ) {
            if ( DebugLog ) Trace( "Bypassing MechDef check {0} because waiting for {1}.", GetName( me ), toLoad.First() );
            return false;
         }
         if ( DebugLog ) Verbo( "Allowing MechDef check {0}.", GetName( me ) );
         depender.Remove( me );
         return true;
      }                 catch ( Exception ex ) { return Error( ex ); } }

      public static void Cleanup_CheckMechDependenciesAfterLoad ( MechDef __instance ) {
         if ( checkingMech == __instance ) checkingMech = null;
      }

      public static void StartLogMechDefDependencies ( MechDef __instance ) {
         if ( UnpatchManager ) return;
         if ( monitoringMech != null ) Warn( "Already logging dependencies for " + GetName( monitoringMech ) );
         monitoringMech = __instance;
         if ( DebugLog ) Verbo( "Start logging dependencies of {0}.", GetName( monitoringMech ) );
         if ( ! depender.ContainsKey( __instance ) )
            depender[ __instance ] = new HashSet<string>();
      }

      public static void StopLogMechDefDependencies () {
         if ( DebugLog ) Trace( "Stop logging dependencies of {0}.", GetName( monitoringMech ) );
         monitoringMech = null;
      }

      // ============ MechComponentDef Bypass ============

      private static MechComponentDef checkingComp;
      private static HashSet<MechComponentDef> LoadedComp;

      public static void Record_CompDependenciesLoaded ( MechComponentDef __instance, bool __result ) {
         if ( __result && __instance.statusEffects != null && __instance.statusEffects.Length > 0 )
            LoadedComp.Add( __instance );
      }

      public static bool Skip_CheckCompDependenciesAfterLoad ( MechComponentDef __instance ) { try {
         if ( UnpatchManager ) return true;
         MechComponentDef me = __instance;
         if ( checkingComp == null ) {
            if ( me.statusEffects != null && me.statusEffects.Length > 0 && LoadedComp.Contains( me ) ) {
               if ( DebugLog ) Trace( "Skipping MechComponentDef check {0} because already finished loading.", GetName( me ) );
               return false;
            }
            if ( DebugLog ) Verbo( "Allowing MechComponentDef verify {0}.", GetName( me ) );
            checkingComp = __instance;
            return true;
         }
         if ( DebugLog ) Trace( "Skipping MechComponentDef check {0} because checking {1}.", GetName( me ), GetName( checkingComp ) );
         return false;
      }                 catch ( Exception ex ) { return Error( ex ); } }

      public static void Cleanup_CheckCompDependenciesAfterLoad ( MechComponentDef __instance ) {
         if ( checkingComp == __instance ) checkingComp = null;
      }

      // ============ Safety System: Kill Switch and Logging ============

      private static bool KillManagerPatch ( DataManager me, Exception err ) { try {
         Error( err );
         Info( "Trying to hand resource loading over and suicide due to exception." );
         List<DataManager.DataManagerLoadRequest> backgroundRequests = (List<DataManager.DataManagerLoadRequest>)
            dmType.GetField( "backgroundRequests", NonPublic | Instance ).GetValue( me );
         List<DataManager.DataManagerLoadRequest> foregroundRequests = (List<DataManager.DataManagerLoadRequest>)
            dmType.GetField( "foregroundRequests", NonPublic | Instance ).GetValue( me );
         if ( backgroundRequests == null || foregroundRequests == null )
            throw new NullReferenceException( "Requests not found; handover aborted." );
         UnpatchManager = true;
         backgroundRequests.AddRange( background.Values );
         background.Clear();
         foregroundRequests.AddRange( foreground.Values );
         Override_Clear( manager );
         Info( "Handover completed. Good luck, commander." );
         return true;
      } catch ( Exception ex ) {
         return Error( ex );
      } }
#pragma warning restore CS0162 // Enable warning of unreachable code

      internal static Logger ModLog = BattleMod.BT_LOG;

      public static void Trace ( object message = null, params object[] args ) { ModLog.Trace( message, args ); }
      public static void Verbo ( object message = null, params object[] args ) { ModLog.Verbo( message, args ); }
      public static void Info  ( object message = null, params object[] args ) { ModLog.Info ( message, args ); }
      public static void Warn  ( object message = null, params object[] args ) { ModLog.Warn ( message, args ); }
      public static bool Error ( object message = null, params object[] args ) { ModLog.Error( message, args ); return true; }
   }
}