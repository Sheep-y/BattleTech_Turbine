using BattleTech;
using BattleTech.Data;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;

namespace Sheepy.BattleTechMod.Turbine {
   using static System.Reflection.BindingFlags;

   #pragma warning disable CS0162 // Disable warning of unreachable code due to DebugLog
   public class Mod : BattleMod {

      // A kill switch to press when any things go wrong during initialisation
      private static bool UnpatchManager = true;

      // Don't timeout my load!
      private static bool NeverTimeout = true;

      // Hack MechDef dependency checking?
      private const bool HackMechDefDependencyCheck = true;

      private const bool DebugLog = false;

      public static void Init () {
         new Mod().Start( ref ModLog );
      }

      private static Type dmType;
      private static MessageCenter center;
      private static Dictionary<string, DataManager.DataManagerLoadRequest> foreground, background;
      private static HashSet<DataManager.DataManagerLoadRequest> foregroundLoading;
      private static float currentTimeout = -1, currentAsyncTimeout = -1;

      public override void ModStarts () {
         // A pretty safe patch that disables invalid or immediately duplicating complete messages.
         Patch( typeof( DataManagerRequestCompleteMessage ).GetConstructors()[0], null, "Skip_DuplicateRequestCompleteMessage" );

         dmType = typeof( DataManager );
         backgroundRequestsCurrentAllowedWeight = dmType.GetField( "backgroundRequestsCurrentAllowedWeight", NonPublic | Instance );
         foregroundRequestsCurrentAllowedWeight = dmType.GetField( "foregroundRequestsCurrentAllowedWeight", NonPublic | Instance );
         prewarmRequests = dmType.GetField( "prewarmRequests", NonPublic | Instance );
         isLoading = dmType.GetField( "isLoading", NonPublic | Instance );
         isLoadingAsync = dmType.GetField( "isLoadingAsync", NonPublic | Instance );
         CreateByResourceType = dmType.GetMethod( "CreateByResourceType", NonPublic | Instance );
         SaveCache = dmType.GetMethod( "SaveCache", NonPublic | Instance );
         if ( backgroundRequestsCurrentAllowedWeight == null || foregroundRequestsCurrentAllowedWeight == null || prewarmRequests == null ||
              isLoading == null || isLoadingAsync == null || CreateByResourceType == null || SaveCache == null )
            throw new NullReferenceException( "One or more DataManager fields not found with reflection." );
         logger = HBS.Logging.Logger.GetLogger( "Data.DataManager" );
         Patch( dmType.GetConstructors()[0], "DataManager_ctor", null );
         Patch( dmType, "Clear", "ClearRequests", null );
         Patch( dmType, "CheckAsyncRequestsComplete", NonPublic, "Override_CheckRequestsComplete", null );
         Patch( dmType, "CheckRequestsComplete", NonPublic, "Override_CheckRequestsComplete", null );
         Patch( dmType, "GraduateBackgroundRequest", NonPublic, "Override_GraduateBackgroundRequest", null );
         Patch( dmType, "NotifyFileLoaded", NonPublic, "Override_NotifyFileLoaded", null );
         Patch( dmType, "NotifyFileLoadedAsync", NonPublic, "Override_NotifyFileLoadedAsync", null );
         Patch( dmType, "NotifyFileLoadFailed", NonPublic, "Override_NotifyFileLoadFailed", null );
         Patch( dmType, "ProcessAsyncRequests", "Override_ProcessAsyncRequests", null );
         Patch( dmType, "ProcessRequests", "Override_ProcessRequests", null );
         Patch( dmType, "RequestResourceAsync_Internal", NonPublic, "Override_RequestResourceAsync_Internal", null );
         Patch( dmType, "RequestResource_Internal", NonPublic, "Override_RequestResource_Internal", null );
         Patch( dmType, "SetLoadRequestWeights", "Override_SetLoadRequestWeights", null );
         Patch( dmType, "UpdateRequestsTimeout", NonPublic, "Override_UpdateRequestsTimeout", null );
         foreground = new Dictionary<string, DataManager.DataManagerLoadRequest>(4096);
         background = new Dictionary<string, DataManager.DataManagerLoadRequest>(4096);
         foregroundLoading = new HashSet<DataManager.DataManagerLoadRequest>();
         UnpatchManager = false;
         LogTime( "Turbine initialised" );

         /* // Code to patch all resource load requests. Good luck with it.
         Type ReqType = typeof( DataManager.ResourceLoadRequest<> );
         // I _hope_ I got everything in the primary assembly.  Not going to check the whole game!
         foreach ( Type nested in typeof( DataManager ).GetNestedTypes( Harmony.AccessTools.all ).Where( e => IsSubclassOfRawGeneric( e, ReqType ) ) ) {
            if ( nested.Name.StartsWith( "ResourceLoadRequest" ) ) continue; // Empty body
            Patch( nested, "Load", "LoadStart", "LoadEnd" );
         }
         */
         if ( HackMechDefDependencyCheck ) {
            Patch( typeof( MechDef ), "CheckDependenciesAfterLoad", "Skip_CheckDependenciesAfterLoad", "Cleanup_CheckDependenciesAfterLoad" );
            Patch( typeof( MechDef ), "RequestDependencies", "StartLogMechDefDependencies", "StopLogMechDefDependencies" );
         }
      }

      /* // Code to test ResourceLoadRequest subclass. https://stackoverflow.com/a/457708/893578 by JaredPar
      private static bool IsSubclassOfRawGeneric(Type toCheck, Type generic) {
         while ( toCheck != null && toCheck != typeof(object) ) {
            var cur = toCheck.IsGenericType ? toCheck.GetGenericTypeDefinition() : toCheck;
            if ( generic == cur ) return true;
            toCheck = toCheck.BaseType;
         }
         return false;
      } /**/

      private static string lastMessage;

      private static void Skip_DuplicateRequestCompleteMessage ( DataManagerRequestCompleteMessage __instance ) {
         if ( String.IsNullOrEmpty( __instance.ResourceId ) ) {
            __instance.hasBeenPublished = true; // Skip publishing empty id
            return;
         }
         string key = GetKey( __instance.ResourceType, __instance.ResourceId );
         if ( lastMessage == key ) {
            if ( DebugLog ) Log( "Skipping successive DataManagerRequestCompleteMessage " + key );
            __instance.hasBeenPublished = true;
         }  else
            lastMessage = key;
      }

      private static Dictionary<MechDef, HashSet<string>> mechDefDependency = new Dictionary<MechDef, HashSet<string>>();
      private static Dictionary<string, HashSet<MechDef>> mechDefLookup = new Dictionary<string, HashSet<MechDef>>();
      private static MechDef monitoringMech, checkingMech;

      public static bool Skip_CheckDependenciesAfterLoad ( MechDef __instance, MessageCenterMessage message ) { try {
         //__state = false;
         if ( UnpatchManager ) return true;
         if ( ! ( message is DataManagerRequestCompleteMessage ) ) return false;
         MechDef me = __instance;
         if ( ! mechDefDependency.TryGetValue( me, out HashSet<string> toLoad ) ) {
            if ( checkingMech == null ) {
               if ( DebugLog ) Log( "Allowing MechDef verify {0}.\n{1}", GetName( me ), new System.Diagnostics.StackTrace( true ).ToString() );
               checkingMech = __instance;
               return true;
            }
            if ( DebugLog ) Log( "Bypassing MechDef check {0} because checking {1}.", GetName( me ), GetName( checkingMech ) );
            return false;
         }
         if ( toLoad.Count > 0 ) {
            if ( DebugLog ) Log( "Bypassing MechDef check {0} because not fully loaded.", GetName( me ) );
            return false;
         }
         if ( DebugLog ) Log( "Allowing MechDef check {0}.", GetName( me ) );
         mechDefDependency.Remove( me );
         return true;
      }                 catch ( Exception ex ) { return Error( ex ); } }

      public static void Cleanup_CheckDependenciesAfterLoad ( MechDef __instance ) {
         if ( checkingMech == __instance ) checkingMech = null;
      }

      public static void StartLogMechDefDependencies ( MechDef __instance ) {
         if ( UnpatchManager ) return;
         if ( monitoringMech != null ) Warn( "Already logging dependencies for " + monitoringMech.ChassisID );
         monitoringMech = __instance;
         if ( DebugLog ) LogTime( "Start logging dependencies of {0}.", GetName( monitoringMech ) );
         if ( ! mechDefDependency.ContainsKey( __instance ) )
            mechDefDependency[ __instance ] = new HashSet<string>();
      }
      
      public static void StopLogMechDefDependencies () {
         if ( DebugLog ) Log( "Stop logging dependencies of {0}.", GetName( monitoringMech ) );
         monitoringMech = null;
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
         bool done = foregroundLoading.All( IsComplete );
         if ( DebugLog && done && foregroundLoading.Count > 0 ) {
            Warn( $"Found {foregroundLoading.Count} unnotified completed requests in loading queue:" );
            foreach ( var request in foregroundLoading ) Log( "   {0} ({1})", GetKey( request ), request.GetType() );
         }
         return done;
      }
      private static bool CheckAsyncRequestsComplete () { return background.Values.All( IsComplete ); }
      private static bool IsComplete ( DataManager.DataManagerLoadRequest e ) { return e.IsComplete(); }

      private static HBS.Logging.ILog logger;
      private static FieldInfo backgroundRequestsCurrentAllowedWeight, foregroundRequestsCurrentAllowedWeight;
      private static FieldInfo prewarmRequests, isLoading, isLoadingAsync;
      private static MethodInfo CreateByResourceType, SaveCache;

      private static string GetName ( MechDef mech ) { return mech == null ? "null" : ( mech.Name + " (" + mech.ChassisID + ")" ); }
      private static string GetKey ( DataManager.DataManagerLoadRequest request ) { return GetKey( request.ResourceType, request.ResourceId ); }
      private static string GetKey ( BattleTechResourceType resourceType, string id ) { return (int) resourceType + "_" + id; }

      private static DataManager manager;

      public static void DataManager_ctor ( MessageCenter messageCenter ) {
         center = messageCenter;
         if ( DebugLog ) LogTime( "DataManager created." );
      }

      public static void ClearRequests ( DataManager __instance ) {
         if ( UnpatchManager ) return;
         if ( DebugLog ) LogTime( "All queues cleared." );
         foreground.Clear();
         background.Clear();
         foregroundLoading.Clear();
         mechDefDependency.Clear();
         mechDefLookup.Clear();
         manager = __instance;
      }
      
      public static bool Override_GraduateBackgroundRequest ( DataManager __instance, ref bool __result, BattleTechResourceType resourceType, string id ) { try {
         if ( UnpatchManager ) return true;
         __result = GraduateBackgroundRequest( __instance, resourceType, id );
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      private static bool GraduateBackgroundRequest ( DataManager me, BattleTechResourceType resourceType, string id ) {
         string key = GetKey( resourceType, id );
         if ( ! background.TryGetValue( key, out DataManager.DataManagerLoadRequest dataManagerLoadRequest ) )
            return false;
         if ( DebugLog ) Log( "Graduating {0} ({1}) from background to foreground.", GetKey( dataManagerLoadRequest ), dataManagerLoadRequest.GetType() );
         dataManagerLoadRequest.SetAsync( false );
         dataManagerLoadRequest.ResetRequestState();
         background.Remove( key );
         foreground.Add( key, dataManagerLoadRequest );
         if ( ! dataManagerLoadRequest.IsComplete() )
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

      public static bool Override_NotifyFileLoaded ( DataManager __instance, DataManager.DataManagerLoadRequest request ) { try {
         if ( UnpatchManager ) return true;
         NotifyFileLoaded( __instance, request );
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      private static void NotifyFileLoaded ( DataManager me, DataManager.DataManagerLoadRequest request ) {
         if ( request.Prewarm != null ) {
            if ( DebugLog ) Log( "Notified Prewarm: " + GetKey( request ) );
            List<PrewarmRequest> pre = (List<PrewarmRequest>) prewarmRequests.GetValue( me );
            pre.Remove( request.Prewarm );
         }
         if ( DebugLog ) Log( "Notified Done: " + GetKey( request ) );
         CheckMechDefDependencies( request );
         if ( request.IsComplete() )
            foregroundLoading.Remove( request );
         if ( CheckRequestsComplete() ) {
            if ( DebugLog ) LogTime( "Foreground requests cleared. Publishing DataManagerLoadCompleteMessage." );
            isLoading.SetValue( me, false );
            SaveCache.Invoke( me, null );
            foreground.Clear();
            foregroundLoading.Clear();
            mechDefDependency.Clear();
            mechDefLookup.Clear();
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
            if ( DebugLog ) Log( "Notified Prewarm Async: " + GetKey( request ) );
            List<PrewarmRequest> pre = (List<PrewarmRequest>) prewarmRequests.GetValue( me );
            pre.Remove( request.Prewarm );
         }
         if ( DebugLog ) Log( "Notified Done Async: " + GetKey( request ) );
         CheckMechDefDependencies( request );
         if ( CheckAsyncRequestsComplete() ) {
            if ( DebugLog ) LogTime( "Background requests cleared. Publishing DataManagerAsyncLoadCompleteMessage." );
            isLoadingAsync.SetValue( me, false );
            SaveCache.Invoke( me, null );
            background.Clear();
            center.PublishMessage( new DataManagerAsyncLoadCompleteMessage() );
         }
      }

      public static bool Override_NotifyFileLoadFailed ( DataManager __instance, DataManager.DataManagerLoadRequest request ) { try {
         if ( UnpatchManager ) return true;
         string key = GetKey( request );
         if ( DebugLog ) Log( "Notified Failed: " + key );
         if ( foreground.Remove( key ) )
            NotifyFileLoaded( __instance, request );
         else if ( background.Remove( key ) )
            NotifyFileLoadedAsync( __instance, request );
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      private static void CheckMechDefDependencies ( DataManager.DataManagerLoadRequest request ) {
         string key = GetKey( request );
         if ( ! mechDefLookup.TryGetValue( key, out HashSet<MechDef> mechs ) ) return;
         mechDefLookup.Remove( key );
         foreach ( MechDef mech in mechs ) {
            if ( mechDefDependency.TryGetValue( mech, out HashSet<string> list ) && list.Remove( key ) ) {
               if ( mechDefDependency[ mech ].Count > 0 ) {
                  if ( DebugLog ) Log( "Found MechDef dependency. Check {0} of {1}. {2} remains.", key, GetName( mech ), mechDefDependency[ mech ].Count );
                  continue;
               }
               if ( DebugLog ) LogTime( "All depencency loaded for {0}.\n{1}", GetName( mech ), new System.Diagnostics.StackTrace( true ).ToString() );
               checkingMech = null;
               mech.CheckDependenciesAfterLoad( new DataManagerLoadCompleteMessage() );
            }
         }
      }

      public static bool Override_ProcessRequests ( DataManager __instance ) { try {
         if ( UnpatchManager ) return true;
         DataManager me = __instance;
         int lightLoad = 0, heavyLoad = 0;
         uint currentAllowedWeight = (uint) foregroundRequestsCurrentAllowedWeight.GetValue( me );
         if ( DebugLog ) Log( "Processing {0} foreground requests", foreground.Count );
         foreach ( DataManager.DataManagerLoadRequest request in foregroundLoading.ToArray() ) {
            if ( lightLoad >= DataManager.MaxConcurrentLoadsLight && heavyLoad >= DataManager.MaxConcurrentLoadsHeavy )
               break;
            request.RequestWeight.SetAllowedWeight( currentAllowedWeight );
            if ( request.State == DataManager.DataManagerLoadRequest.RequestState.Requested ) {
               if ( request.IsMemoryRequest )
                  me.RemoveObjectOfType( request.ResourceId, request.ResourceType );
               if ( request.AlreadyLoaded ) {
                  if ( ! request.DependenciesLoaded( currentAllowedWeight ) ) {
                     DataManager.ILoadDependencies dependencyLoader = request.TryGetLoadDependencies();
                     if ( dependencyLoader != null ) {
                        request.RequestWeight.SetAllowedWeight( currentAllowedWeight );
                        dependencyLoader.RequestDependencies( me, () => {
                           if ( dependencyLoader.DependenciesLoaded( request.RequestWeight.AllowedWeight ) )
                              request.NotifyLoadComplete();
                        }, request );
                        if ( request.RequestWeight.RequestWeight == 10u ) {
                           if ( DataManager.MaxConcurrentLoadsLight > 0 )
                              lightLoad++;
                        } else if ( DataManager.MaxConcurrentLoadsHeavy > 0 )
                           heavyLoad++;
                        isLoading.SetValue( me, true );
                        me.ResetRequestsTimeout();
                     }
                  } else
                     request.NotifyLoadComplete();
               } else {
                  if ( lightLoad >= DataManager.MaxConcurrentLoadsLight && heavyLoad >= DataManager.MaxConcurrentLoadsHeavy )
                     break;
                  if ( ! request.ManifestEntryValid ) {
                     logger.LogError( string.Format( "LoadRequest for {0} of type {1} has an invalid manifest entry. Any requests for this object will fail.", request.ResourceId, request.ResourceType ) );
                     request.NotifyLoadFailed();
                  } else if ( !request.RequestWeight.RequestAllowed ) {
                     request.NotifyLoadComplete();
                  } else {
                     if ( request.RequestWeight.RequestWeight == 10u ) {
                        if ( DataManager.MaxConcurrentLoadsLight > 0 )
                           lightLoad++;
                     } else if ( DataManager.MaxConcurrentLoadsHeavy > 0 )
                        heavyLoad++;
                     isLoading.SetValue( me, true );
                     if ( DebugLog ) Log( "Loading {0}.", GetKey( request ) );
                     request.Load();
                     me.ResetRequestsTimeout();
                  }
               }
            }
         }
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      public static bool Override_ProcessAsyncRequests ( DataManager __instance ) {
         if ( UnpatchManager ) return true;
         if ( background.Count < 0 ) return false; // Early abort before reflection
         DataManager me = __instance;
         uint currentAllowedWeight = (uint) backgroundRequestsCurrentAllowedWeight.GetValue( me );
         if ( DebugLog ) Log( "Processing {0} background requests", background.Count );
         foreach ( DataManager.DataManagerLoadRequest request in background.Values ) {
            request.RequestWeight.SetAllowedWeight( currentAllowedWeight );
            DataManager.DataManagerLoadRequest.RequestState state = request.State;
            if ( state == DataManager.DataManagerLoadRequest.RequestState.Processing ) return false;
            if ( state == DataManager.DataManagerLoadRequest.RequestState.RequestedAsync ) {
               if ( request.IsMemoryRequest )
                  me.RemoveObjectOfType( request.ResourceId, request.ResourceType );
               if ( request.AlreadyLoaded ) {
                  if ( !request.DependenciesLoaded( currentAllowedWeight ) ) {
                     DataManager.ILoadDependencies dependencyLoader = request.TryGetLoadDependencies();
                     if ( dependencyLoader != null ) {
                        request.RequestWeight.SetAllowedWeight( currentAllowedWeight );
                        dependencyLoader.RequestDependencies( me, () => {
                           if ( dependencyLoader.DependenciesLoaded( request.RequestWeight.AllowedWeight ) )
                              request.NotifyLoadComplete();
                        }, request );
                        isLoadingAsync.SetValue( me, true );
                        me.ResetAsyncRequestsTimeout();
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
                  if ( DebugLog ) Log( "Loading Async {0}.", GetKey( request ) );
                  request.Load();
                  me.ResetAsyncRequestsTimeout();
               }
               return false;
            }
         }
         return false;
      }

      public static bool Override_RequestResourceAsync_Internal ( DataManager __instance, BattleTechResourceType resourceType, string identifier, PrewarmRequest prewarm ) { try {
         if ( UnpatchManager || string.IsNullOrEmpty( identifier ) ) return false;
         DataManager me = __instance;
         string key = GetKey( resourceType, identifier );
         background.TryGetValue( key, out DataManager.DataManagerLoadRequest dataManagerLoadRequest );
         if ( dataManagerLoadRequest != null ) {
            if ( dataManagerLoadRequest.State == DataManager.DataManagerLoadRequest.RequestState.Complete ) {
               if ( !dataManagerLoadRequest.DependenciesLoaded( (uint) backgroundRequestsCurrentAllowedWeight.GetValue( me ) ) ) {
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
            if ( DebugLog ) Log( "Queued Async: {0} ({1}) ", key, dataManagerLoadRequest.GetType() );
            background.Add( key, dataManagerLoadRequest );
         }
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      private static BattleTechResourceType lastResourceType;
      private static string lastIdentifier;

      public static bool Override_RequestResource_Internal ( DataManager __instance, BattleTechResourceType resourceType, string identifier, PrewarmRequest prewarm, bool allowRequestStacking ) { try {
         if ( UnpatchManager || string.IsNullOrEmpty( identifier ) ) return false;
         // Quickly skip duplicate request
         if ( resourceType == lastResourceType && identifier == lastIdentifier ) return false;
         lastResourceType = resourceType;
         lastIdentifier = identifier;
         DataManager me = __instance;
         string key = GetKey( resourceType, identifier );
         if ( monitoringMech != null ) {
            if ( ! mechDefLookup.TryGetValue( key, out HashSet<MechDef> depList ) )
               mechDefLookup[ key ] = depList = new HashSet<MechDef>();
            if ( ! depList.Contains( monitoringMech ) ) {
               if ( DebugLog ) Log( "   " + monitoringMech + " requested " + key );
               depList.Add( monitoringMech );
               mechDefDependency[ monitoringMech ].Add( key );
            }
         }
         foreground.TryGetValue( key, out DataManager.DataManagerLoadRequest dataManagerLoadRequest );
         if ( dataManagerLoadRequest != null ) {
            if ( dataManagerLoadRequest.State != DataManager.DataManagerLoadRequest.RequestState.Complete || !dataManagerLoadRequest.DependenciesLoaded( dataManagerLoadRequest.RequestWeight.RequestWeight ) ) {
               if ( allowRequestStacking )
                  dataManagerLoadRequest.IncrementCacheCount();
            } else
               Override_NotifyFileLoaded( me, dataManagerLoadRequest );
            return false;
         }
         bool movedToForeground = GraduateBackgroundRequest( me, resourceType, identifier);
         bool skipLoad = false;
         bool isTemplate = identifier.ToLowerInvariant().Contains("template");
         if ( !movedToForeground && !skipLoad && !isTemplate ) {
            dataManagerLoadRequest = (DataManager.DataManagerLoadRequest) CreateByResourceType.Invoke( me, new object[]{ resourceType, identifier, prewarm } );
            if ( DebugLog ) Log( "Queued: {0} ({1}) ", key, dataManagerLoadRequest.GetType() );
            foreground.Add( key, dataManagerLoadRequest );
            if ( ! dataManagerLoadRequest.IsComplete() ) {
               foregroundLoading.Add( dataManagerLoadRequest );
            }
         }
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      public static bool Override_SetLoadRequestWeights ( DataManager __instance, uint foregroundRequestWeight, uint backgroundRequestWeight ) { try {
         if ( UnpatchManager ) return true;
         if ( DebugLog ) Log( "Set LoadRequestWeights {0}/{1} on {2}/{3} loading foreground/background requests.", foregroundRequestWeight, backgroundRequestWeight, foregroundLoading.Count, background.Count );
         foregroundRequestsCurrentAllowedWeight.SetValue( __instance, foregroundRequestWeight );
         backgroundRequestsCurrentAllowedWeight.SetValue( __instance, backgroundRequestWeight );
         foreach ( DataManager.DataManagerLoadRequest dataManagerLoadRequest in foregroundLoading )
            if ( foregroundRequestWeight > dataManagerLoadRequest.RequestWeight.AllowedWeight )
               dataManagerLoadRequest.RequestWeight.SetAllowedWeight( foregroundRequestWeight );
         foreach ( DataManager.DataManagerLoadRequest dataManagerLoadRequest in background.Values )
            if ( backgroundRequestWeight > dataManagerLoadRequest.RequestWeight.AllowedWeight )
               dataManagerLoadRequest.RequestWeight.SetAllowedWeight( backgroundRequestWeight );
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      public static bool Override_UpdateRequestsTimeout ( DataManager __instance, float deltaTime ) { try {
         if ( UnpatchManager ) return true;
         if ( NeverTimeout ) return false;
         DataManager me = __instance;
         if ( currentTimeout >= 0f ) {
            if ( foregroundLoading.Any( IsProcessing ) ) {
               if ( DebugLog ) Warn( "Foreground request timeout." );
               DataManager.DataManagerLoadRequest[] list = foregroundLoading.Where( IsProcessing ).ToArray();
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

      private static bool KillManagerPatch ( DataManager me, Exception err ) { try {
         Error( err );
         LogTime( "Trying to hand resource loading over and suicide due to exception." );
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
         foreground.Clear();
         foregroundLoading.Clear();
         mechDefDependency.Clear();
         mechDefLookup.Clear();
         Log( "Handover completed. Good luck, commander." );
         return true;
      } catch ( Exception ex ) {
         return Error( ex );
      } }

      // ============ Logging ============

      internal static Logger ModLog = Logger.BT_LOG;

      public static void Log ( object message ) { ModLog.Log( message ); }
      public static void Log ( string message = "" ) { ModLog.Log( message ); }
      public static void Log ( string message, params object[] args ) { ModLog.Log( message, args ); }

      public static void LogTime ( string message = "" ) { ModLog.Log( DateTime.Now.ToString( "mm:ss" ) + " " + message ); }
      public static void LogTime ( string message, params object[] args ) { ModLog.Log( DateTime.Now.ToString( "mm:ss" ) + " " + message, args ); }

      public static void Warn ( object message ) { ModLog.Warn( message ); }
      public static void Warn ( string message ) { ModLog.Warn( message ); }
      public static void Warn ( string message, params object[] args ) { ModLog.Warn( message, args ); }

      public static bool Error ( object message ) { return ModLog.Error( message ); }
      public static void Error ( string message ) { ModLog.Error( message ); }
      public static void Error ( string message, params object[] args ) { ModLog.Error( message, args ); }
   }
   #pragma warning restore CS0162 // Enable warning of unreachable code
}