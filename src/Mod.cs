using BattleTech;
using BattleTech.Data;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;

namespace Sheepy.BattleTechMod.Turbine {
   using static System.Reflection.BindingFlags;

   public class Mod : BattleMod {

      // A kill switch to press when any things go wrong during initialisation
      private static bool UnpatchManager = true;

      // Hack MechDef dependency checking?
      private const bool HackMechDefDependencyCheck = true;

      public static void Init () {
         new Mod().Start( ref ModLog );
      }

      private static Type dmType;
      private static MessageCenter center;
      private static Dictionary<string, DataManager.DataManagerLoadRequest> foreground, background;
      private static HashSet<DataManager.DataManagerLoadRequest> foregroundLoading, backgroundLoading;
      private static float currentTimeout = -1, currentAsyncTimeout = -1;

      public override void ModStarts () {
         //Logger.Delete();
         //Logger = Logger.BT_LOG;
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
         backgroundLoading = new HashSet<DataManager.DataManagerLoadRequest>();
         UnpatchManager = false;
         Log( "Turbine initialised" );
         /*
         Type ReqType = typeof( DataManager.ResourceLoadRequest<> );
         // I _hope_ I got everything in the primary assembly.  Not going to check the whole game!
         foreach ( Type nested in typeof( DataManager ).GetNestedTypes( Harmony.AccessTools.all ).Where( e => IsSubclassOfRawGeneric( e, ReqType ) ) ) {
            if ( nested.Name.StartsWith( "ResourceLoadRequest" ) ) continue; // Empty body
            Patch( nested, "Load", "LoadStart", "LoadEnd" );
         }
         */
         Patch( typeof( DataManagerRequestCompleteMessage ).GetConstructors()[0], null, "Skip_DuplicateRequestCompleteMessage" );
         if ( HackMechDefDependencyCheck ) {
            Patch( typeof( MechDef ), "CheckDependenciesAfterLoad", "Skip_CheckDependenciesAfterLoad", "Cleanup_CheckDependenciesAfterLoad" );
            Patch( typeof( MechDef ), "RequestDependencies", "StartLogMechDefDependencies", "StopLogMechDefDependencies" );
         }
      }

      // https://stackoverflow.com/a/457708/893578 by JaredPar
      private static bool IsSubclassOfRawGeneric(Type toCheck, Type generic) {
         while ( toCheck != null && toCheck != typeof(object) ) {
            var cur = toCheck.IsGenericType ? toCheck.GetGenericTypeDefinition() : toCheck;
            if ( generic == cur ) return true;
            toCheck = toCheck.BaseType;
         }
         return false;
      }

      private static string lastMessage;

      private static void Skip_DuplicateRequestCompleteMessage ( DataManagerRequestCompleteMessage __instance ) {
         if ( UnpatchManager ) return;
         if ( String.IsNullOrEmpty( __instance.ResourceId ) ) {
            __instance.hasBeenPublished = true; // Skip publishing empty id
            return;
         }
         string key = GetKey( __instance.ResourceType, __instance.ResourceId );
         if ( lastMessage == key )
            __instance.hasBeenPublished = true;
         else
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
            //Log( "MechDef re-check After Done " + me.Name + " / " + me.ChassisID );
            //Log( new System.Diagnostics.StackTrace( true ).ToString() );
            if ( checkingMech == null ) {
               checkingMech = __instance;
               return true;
            }
            return false;
         }
         if ( toLoad.Count > 0 ) return false;
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
         //Log( "Logging dependencies for " + monitoringMech.Name + " / " + monitoringMech.ChassisID );
         if ( ! mechDefDependency.ContainsKey( __instance ) )
            mechDefDependency[ __instance ] = new HashSet<string>();
      }
      
      public static void StopLogMechDefDependencies () {
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
         /**
         if ( done && foregroundLoading.Count > 0 ) {
            Warn( $"Found {foregroundLoading.Count} completed requests in loading queue" );
            foreach ( var request in foregroundLoading )
               Log( GetKey( request ) + " = " + request.GetType() );
         }
         /**/
         return done;
      }
      private static bool CheckAsyncRequestsComplete () { return backgroundLoading.All( IsComplete ); }
      private static bool IsComplete ( DataManager.DataManagerLoadRequest e ) { return e.IsComplete(); }

      private static HBS.Logging.ILog logger;
      private static FieldInfo backgroundRequestsCurrentAllowedWeight, foregroundRequestsCurrentAllowedWeight;
      private static FieldInfo prewarmRequests, isLoading, isLoadingAsync;
      private static MethodInfo CreateByResourceType, SaveCache;

      private static string GetKey ( DataManager.DataManagerLoadRequest request ) { return GetKey( request.ResourceType, request.ResourceId ); }
      private static string GetKey ( BattleTechResourceType resourceType, string id ) { return (int) resourceType + "_" + id; }

      private static DataManager manager;

      public static void DataManager_ctor ( MessageCenter messageCenter ) {
         center = messageCenter;
      }

      public static void ClearRequests ( DataManager __instance ) {
         if ( UnpatchManager ) return;
         foreground.Clear();
         background.Clear();
         foregroundLoading.Clear();
         backgroundLoading.Clear();
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
         //Log( "Graduate: " + GetKey( dataManagerLoadRequest ) + " = " + dataManagerLoadRequest.GetType() );
         dataManagerLoadRequest.SetAsync( false );
         dataManagerLoadRequest.ResetRequestState();
         background.Remove( key );
         foreground.Add( key, dataManagerLoadRequest );
         if ( backgroundLoading.Remove( dataManagerLoadRequest ) )
            foregroundLoading.Add( dataManagerLoadRequest );
         bool wasLoadingAsync = (bool) isLoadingAsync.GetValue( me );
         bool nowLoadingAsync = ! CheckAsyncRequestsComplete();
         if ( nowLoadingAsync != wasLoadingAsync ) {
            isLoadingAsync.SetValue( me, nowLoadingAsync );
            if ( wasLoadingAsync ) {
               SaveCache.Invoke( me, null );
               background.Clear();
               backgroundLoading.Clear();
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
            //Log( "Done Prewarm: " + GetKey( request ) );
            List<PrewarmRequest> pre = (List<PrewarmRequest>) prewarmRequests.GetValue( me );
            pre.Remove( request.Prewarm );
         }
         string key = GetKey( request );
         //Log( "Done: " + GetKey( request ) );
         if ( mechDefLookup.TryGetValue( key, out HashSet<MechDef> mechs ) ) {
            mechDefLookup.Remove( key );
            foreach ( MechDef mech in mechs ) {
               if ( mechDefDependency.TryGetValue( mech, out HashSet<string> list ) &&  list.Remove( key ) ) {
                  //Log( "Remove " + key + " from " + mech.Name + " / " + mech.ChassisID );
                  if ( mechDefDependency[ mech ].Count <= 0 ) {
                     checkingMech = null;
                     mech.CheckDependenciesAfterLoad( new DataManagerLoadCompleteMessage() );
                     //Log( "ALL LOADED " + mech.Name + " / " + mech.ChassisID );
                     //Log( new System.Diagnostics.StackTrace( true ).ToString() );
                  }
               }
            }
         }
         if ( request.IsComplete() )
            foregroundLoading.Remove( request );
         if ( CheckRequestsComplete() ) {
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
            //Log( "Done Prewarm: " + GetKey( request ) );
            List<PrewarmRequest> pre = (List<PrewarmRequest>) prewarmRequests.GetValue( me );
            pre.Remove( request.Prewarm );
         }
         //Log( "Done Async: " + GetKey( request ) );
         if ( request.IsComplete() )
            backgroundLoading.Remove( request );
         if ( CheckAsyncRequestsComplete() ) {
            isLoadingAsync.SetValue( me, false );
            SaveCache.Invoke( me, null );
            background.Clear();
            backgroundLoading.Clear();
            center.PublishMessage( new DataManagerAsyncLoadCompleteMessage() );
         }
      }

      public static bool Override_NotifyFileLoadFailed ( DataManager __instance, DataManager.DataManagerLoadRequest request ) { try {
         if ( UnpatchManager ) return true;
         string key = GetKey( request );
         if ( foreground.Remove( key ) )
            NotifyFileLoaded( __instance, request );
         else if ( background.Remove( key ) )
            NotifyFileLoadedAsync( __instance, request );
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      public static bool Override_ProcessRequests ( DataManager __instance ) { try {
         if ( UnpatchManager ) return true;
         DataManager me = __instance;
         int lightLoad = 0, heavyLoad = 0;
         uint currentAllowedWeight = (uint) foregroundRequestsCurrentAllowedWeight.GetValue( me );
         foreach ( DataManager.DataManagerLoadRequest request in foreground.Values.ToArray() ) {
            if ( lightLoad >= DataManager.MaxConcurrentLoadsLight && heavyLoad >= DataManager.MaxConcurrentLoadsHeavy )
               break;
            request.RequestWeight.SetAllowedWeight( currentAllowedWeight );
            if ( request.State == DataManager.DataManagerLoadRequest.RequestState.Requested ) {
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
         DataManager me = __instance;
         uint currentAllowedWeight = (uint) backgroundRequestsCurrentAllowedWeight.GetValue( me );
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
            //Log( "Queue Async: " + GetKey( dataManagerLoadRequest ) + " = " + dataManagerLoadRequest.GetType() + " @ " + dataManagerLoadRequest.IsComplete() );
            background.Add( key, dataManagerLoadRequest );
            if ( ! dataManagerLoadRequest.IsComplete() )
               backgroundLoading.Add( dataManagerLoadRequest );
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
            if ( ! mechDefLookup.ContainsKey( key ) ) mechDefLookup[ key ] = new HashSet<MechDef>();
            //if ( ! mechDefLookup[ key ].Contains( monitoringMech ) ) Log( "   " + monitoringMech + " requested " + key );
            mechDefLookup[ key ].Add( monitoringMech );
            mechDefDependency[ monitoringMech ].Add( key );
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
            //Log( "Queue: " + GetKey( dataManagerLoadRequest ) + " = " + dataManagerLoadRequest.GetType() + " @ " + dataManagerLoadRequest.IsComplete() );
            foreground.Add( key, dataManagerLoadRequest );
            if ( ! dataManagerLoadRequest.IsComplete() ) {
               foregroundLoading.Add( dataManagerLoadRequest );
            }
         }
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      public static bool Override_SetLoadRequestWeights ( DataManager __instance, uint foregroundRequestWeight, uint backgroundRequestWeight ) { try {
         if ( UnpatchManager ) return true;
         foregroundRequestsCurrentAllowedWeight.SetValue( __instance, foregroundRequestWeight );
         backgroundRequestsCurrentAllowedWeight.SetValue( __instance, backgroundRequestWeight );
         foreach ( DataManager.DataManagerLoadRequest dataManagerLoadRequest in foregroundLoading )
            if ( foregroundRequestWeight > dataManagerLoadRequest.RequestWeight.AllowedWeight )
               dataManagerLoadRequest.RequestWeight.SetAllowedWeight( foregroundRequestWeight );
         foreach ( DataManager.DataManagerLoadRequest dataManagerLoadRequest in backgroundLoading )
            if ( backgroundRequestWeight > dataManagerLoadRequest.RequestWeight.AllowedWeight )
               dataManagerLoadRequest.RequestWeight.SetAllowedWeight( backgroundRequestWeight );
         return false;
      }                 catch ( Exception ex ) { return KillManagerPatch( __instance, ex ); } }

      public static bool Override_UpdateRequestsTimeout ( DataManager __instance, float deltaTime ) { try {
         if ( UnpatchManager ) return true;
         DataManager me = __instance;
         if ( currentTimeout >= 0f ) {
            if ( foregroundLoading.Any( IsProcessing ) ) {
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
         if ( currentAsyncTimeout >= 0f && backgroundLoading.Count > 0 ) {
            currentAsyncTimeout += deltaTime;
            if ( currentAsyncTimeout > 20f ) {
               DataManager.DataManagerLoadRequest dataManagerLoadRequest = backgroundLoading.First( IsProcessing );
               if ( dataManagerLoadRequest != null ) {
                  logger.LogWarning( string.Format( "DataManager ASYNC Request for {0} has taken too long. Cancelling request. Your load will probably fail", dataManagerLoadRequest.ResourceId ) );
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
         Log( "Trying to hand resource loading over due to exception." );
         List<DataManager.DataManagerLoadRequest> backgroundRequests = (List<DataManager.DataManagerLoadRequest>) 
            dmType.GetField( "backgroundRequests", NonPublic | Instance ).GetValue( me );
         List<DataManager.DataManagerLoadRequest> foregroundRequests = (List<DataManager.DataManagerLoadRequest>) 
            dmType.GetField( "foregroundRequests", NonPublic | Instance ).GetValue( me );
         if ( backgroundRequests == null || foregroundRequests == null ) 
            throw new NullReferenceException( "Requests not found; handover safely aborted." );
         UnpatchManager = true;
         backgroundRequests.AddRange( background.Values );
         background.Clear();
         backgroundLoading.Clear();
         foregroundRequests.AddRange( foreground.Values );
         foreground.Clear();
         foregroundLoading.Clear();
         mechDefDependency.Clear();
         mechDefLookup.Clear();
         return true;
      } catch ( Exception ex ) {
         Log( "Exception during handover." );
         return Error( ex );
      } }

      // ============ Logging ============

      internal static Logger ModLog = Logger.BT_LOG;

      public static void Log ( object message ) { ModLog.Log( message ); }
      public static void Log ( string message = "" ) { ModLog.Log( message ); }
      public static void Log ( string message, params object[] args ) { ModLog.Log( message, args ); }

      public static void Warn ( object message ) { ModLog.Warn( message ); }
      public static void Warn ( string message ) { ModLog.Warn( message ); }
      public static void Warn ( string message, params object[] args ) { ModLog.Warn( message, args ); }

      public static bool Error ( object message ) { return ModLog.Error( message ); }
      public static void Error ( string message ) { ModLog.Error( message ); }
      public static void Error ( string message, params object[] args ) { ModLog.Error( message, args ); }
   }
}