using BattleTech;
using BattleTech.UI;
using Harmony;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Sheepy.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using static System.Reflection.BindingFlags;

namespace Sheepy.BattleTechMod {

   public abstract class BattleMod : BattleModModule {

      public static readonly Logger BTML_LOG = new Logger( "Mods/BTModLoader.log" );
      public static readonly Logger BT_LOG = new Logger( "BattleTech_Data/output_log.txt" );

      // Basic mod info for public access, will auto load from assembly then mod.json (if exists)
      public string Version { get; protected set; } = "Unknown";

      protected BattleMod () {
         ReadBasicModInfo();
      }

      public void Start () { Logger log = Log; Start( ref log ); }
      public void Start ( ref Logger log ) {
         CurrentMod = this;
         TryRun( Setup ); // May be overloaded
         if ( log != Log ) 
            log = Log;
         Add( this );
         PatchBattleMods();
         CurrentMod = null;
      }

      public string BaseDir { get; protected set; }
      private string _LogDir;
      public string LogDir {
         get { return _LogDir; }
         protected set {
            _LogDir = value;
            Log = new Logger( GetLogFile() );
         }
      }
      public HarmonyInstance ModHarmony { get; internal set; }

      // ============ Setup ============

      /*
      private static List<BattleMod> modScopes = new List<BattleMod>();
      private void PushScope () { modScopes.Add( this ); }
      private void PopScope () { modScopes.RemoveAt( modScopes.Count - 1 ); }
      internal static BattleMod CurrentMod { get { return modScopes.LastOrDefault(); } }
      */
      internal static BattleMod CurrentMod;

#pragma warning disable CS0649 // Disable "field never set" warnings since they are set by JsonConvert.
      private class ModInfo { public string Name;  public string Version; }
#pragma warning restore CS0649

      // Fill in blanks with Assembly values, then read from mod.json
      private void ReadBasicModInfo () { TryRun( Log, () => {
         Assembly file = GetType().Assembly;
         Id = GetType().Namespace;
         Name = file.GetName().Name;
         BaseDir = Path.GetDirectoryName( file.Location ) + "/"; 
         string mod_info_file = BaseDir + "mod.json";
         if ( File.Exists( mod_info_file ) ) TryRun( Log, () => {
            ModInfo info = JsonConvert.DeserializeObject<ModInfo>( File.ReadAllText( mod_info_file ) );
            if ( ! string.IsNullOrEmpty( info.Name ) )
               Name = info.Name;
            if ( ! string.IsNullOrEmpty( info.Version ) )
               Version = info.Version;
         } );
         LogDir = BaseDir; // Create Logger after Name is read from mod.json
      } ); }

      // Override this method to override Namd, Id, or Logger. Remember to call this base method!
      protected virtual void Setup () {
         Log.Delete();
         Log.Info( "{0:yyyy-MM-dd} Loading {1} Version {2} @ {3}", DateTime.Now, Name, Version, BaseDir );
         Log.Info( "Game Version {0}, Harmony Version {1}" + Environment.NewLine, VersionInfo.ProductVersion, typeof(HarmonyInstance).Assembly.GetName().Version );
      }

      public static string Idify ( string text ) { return Join( string.Empty, new Regex( "\\W+" ).Split( text ), UppercaseFirst ); }

      protected virtual string GetLogFile () {
         return LogDir + "Log_" + Idify( Name ) + ".txt";
      }

      // Load settings from settings.json, call SanitizeSettings, and create/overwrite it if the content is different.
      protected virtual void LoadSettings <Settings> ( ref Settings settings, Action<Settings> sanitise = null ) {
         string file = BaseDir + "settings.json", fileText = string.Empty;
         Settings config = settings;
         if ( File.Exists( file ) ) TryRun( () => {
            fileText = File.ReadAllText( file );
            if ( fileText.Contains( "\"Name\"" ) && fileText.Contains( "\"DLL\"" ) && fileText.Contains( "\"Settings\"" ) ) TryRun( Log, () => {
               JObject modInfo = JObject.Parse( fileText );
               if ( modInfo.TryGetValue( "Settings", out JToken embedded ) )
                  fileText = embedded.ToString( Formatting.None );
            } );
            config = JsonConvert.DeserializeObject<Settings>( fileText );
         } );
         if ( sanitise != null )
            TryRun( () => sanitise( config ) );

         ThreadPool.QueueUserWorkItem( ( obj ) => {
            string sanitised;
            sanitised = JsonConvert.SerializeObject( obj, Formatting.Indented, new JsonSerializerSettings { ContractResolver = new BattleJsonContract() } );
            sanitised = Regex.Replace( sanitised, @"(?<=\d\.\d+)0+(?=,\r?\n)", "" );
            Log.Info( "WARNING: Do NOT change settings here. This is just a log." );
            Log.Info( "Loaded Settings: " + sanitised );
            Log.Info( "WARNING: Do NOT change settings here. This is just a log." ); // Yes. It is intentionally repeated.
            string commented = BattleJsonContract.FormatSettingJsonText( obj.GetType(), sanitised );
            if ( commented != fileText ) { // Can be triggered by comment or field updates, not necessary sanitisation.
               Log.Info( "Background: Updating " + file );
               SaveSettings( commented );
            }
         }, typeof( object ).GetMethod( "MemberwiseClone", NonPublic | Instance ).Invoke( config, null ) );
         settings = config;
      }

      protected void SaveSettings ( Settings settings_object ) {
         SaveSettings( JsonConvert.SerializeObject( settings_object, Formatting.Indented ) );
      }

      private void SaveSettings ( string settings ) {
         TryRun( Log, () => File.WriteAllText( BaseDir + "settings.json", settings ) );
      }

      // ============ Execution ============

      private static Dictionary<BattleMod, List<BattleModModule>> modules = new Dictionary<BattleMod, List<BattleModModule>>();

      public BattleMod Add ( BattleModModule module ) {
         if ( ! modules.TryGetValue( this, out List<BattleModModule> list ) )
            modules.Add( this, list = new List<BattleModModule>() );
         if ( ! list.Contains( module ) ) {
            if ( module != this )
               if ( module.Id == Id ) module.Id += "." + Idify( module.Name );
            list.Add( module );
            TryRun( Log, module.ModStarts );
         }
         return this;
      }

      private static bool GameStartPatched = false;

      public void PatchBattleMods () {
         if ( GameStartPatched ) return;
         Patch( typeof( UnityGameInstance ).GetMethod( "InitUserSettings", Instance | NonPublic ), null, typeof( BattleMod ).GetMethod( "RunGameStarts", Static | NonPublic ) );
         Patch( typeof( SimGameState ).GetMethod( "Init" ), null, typeof( BattleMod ).GetMethod( "RunCampaignStarts", Static | NonPublic ) );
         Patch( typeof( CombatHUD ).GetMethod( "Init", new Type[]{ typeof( CombatGameState ) } ), null, typeof( BattleMod ).GetMethod( "RunCombatStarts", Static | NonPublic ) );
         Patch( typeof( CombatHUD ).GetMethod( "OnCombatGameDestroyed", new Type[]{} ), null, typeof( BattleMod ).GetMethod( "RunCombatEnds", Static | NonPublic ) );
         GameStartPatched = true;
      }

      private static bool CalledGameStartsOnce = false;
      private static void RunGameStarts () {
         BattleTechGame = UnityGameInstance.BattleTechGame;
         if ( ! CalledGameStartsOnce ) {
            CallAllModules( module => module.GameStartsOnce() );
            CalledGameStartsOnce = true;
         }
         CallAllModules( module => module.GameStarts() );
      }

      private static bool CalledCampaignStartsOnce = false;
      private static void RunCampaignStarts () {
         Simulation = BattleTechGame?.Simulation;
         SimulationConstants = Simulation?.Constants;
         if ( ! CalledCampaignStartsOnce ) {
            CallAllModules( module => module.CampaignStartsOnce() );
            CalledCampaignStartsOnce = true;
         }
         CallAllModules( module => module.CampaignStarts() );
      }
      
      private static bool CalledCombatStartsOnce = false;
      private static void RunCombatStarts ( CombatHUD __instance ) {
         HUD = __instance;
         Combat = BattleTechGame?.Combat;
         CombatConstants = Combat?.Constants;
         if ( ! CalledCombatStartsOnce ) {
            CallAllModules( module => module.CombatStartsOnce() );
            CalledCombatStartsOnce = true;
         }
         CallAllModules( module => module.CombatStarts() );
      }

      private static void RunCombatEnds ( CombatHUD __instance ) {
         CallAllModules( module => module.CombatEnds() );
      }

      private static void CallAllModules ( Action<BattleModModule> task ) {
         foreach ( var mod in modules ) {
            foreach ( BattleModModule module in mod.Value ) try {
               task( module );
            } catch ( Exception ex ) { 
               mod.Key.Log.Error( ex ); 
            }
         }
      }

      private static HashSet<string> modList;
      public static string[] GetModList() {
         if ( modList == null ) {
            if ( BattleTechGame == null )
               throw new InvalidOperationException( "Mod List is not known until GameStartsOnce." );
            modList = new HashSet<string>();
            try {
               foreach ( MethodBase method in PatchProcessor.AllPatchedMethods() )
                  modList.UnionWith( PatchProcessor.GetPatchInfo( method ).Owners );
               // Some mods may not leave a harmony trace and can only be parsed from log
               Regex regx = new Regex( " in type \"([^\"]+)\"", RegexOptions.Compiled );
               foreach ( string line in File.ReadAllLines( "Mods/BTModLoader.log" ) ) {
                  Match match = regx.Match( line );
                  if ( match.Success ) modList.Add( match.Groups[1].Value );
               }
            } catch ( Exception ex ) {
               BattleMod.BTML_LOG.Error( ex );
            }
         }
         return modList.ToArray();
      }

      public static bool FoundMod ( params string[] mods ) {
         if ( modList == null ) GetModList();
         foreach ( string mod in mods )
            if ( modList.Contains( mod ) ) return true;
         return false;
      }
   }

   public abstract class BattleModModule {
      
      // Set on GameStarts
      public static GameInstance BattleTechGame { get; internal set; }
      // Set on CampaignStarts
      public static SimGameState Simulation { get; internal set; }
      public static SimGameConstants SimulationConstants { get; internal set; }
      // Set on CombatStarts
      public static CombatGameState Combat { get; internal set; }
      public static CombatGameConstants CombatConstants { get; internal set; }
      public static CombatHUD HUD { get; internal set; }

      public virtual void ModStarts () {}
      public virtual void GameStartsOnce () { }
      public virtual void GameStarts () {}
      public virtual void CampaignStartsOnce () { }
      public virtual void CampaignStarts () {}
      public virtual void CombatStartsOnce () {}
      public virtual void CombatStarts () {}
      public virtual void CombatEnds () {}

      protected BattleMod Mod { get; private set; }

      // ============ Basic ============

      public BattleModModule () {
         if ( this is BattleMod modbase )
            Mod = modbase;
         else {
            Mod = BattleMod.CurrentMod;
            if ( Mod == null )
               throw new ApplicationException( "Mod module should be created in BattleMod.ModStart()." );
            Id = Mod.Id;
            Log = Mod.Log;
         }
      }

      public string Id { get; protected internal set; } = "org.example.mod.module";
      public string Name { get; protected internal set; } = "Module";
      
      private Logger _Logger;
      protected Logger Log {
         get { return _Logger ?? BattleMod.BTML_LOG; }
         set { _Logger = value; }
      }

      // ============ Harmony ============

      /* Find and create a HarmonyMethod from this class. method must be public and has unique name. */
      protected HarmonyMethod MakePatch ( string method ) {
         if ( method == null ) return null;
         MethodInfo mi = GetType().GetMethod( method, Static | Public | NonPublic );
         if ( mi == null ) {
            Log.Error( "Cannot find patch method " + method );
            return null;
         }
         return new HarmonyMethod( mi );
      }

      protected void Patch ( Type patchedClass, string patchedMethod, string prefix, string postfix ) {
         Patch( patchedClass, patchedMethod, Public | Instance, (Type[]) null, prefix, postfix );
      }

      protected void Patch ( Type patchedClass, string patchedMethod, Type parameterType, string prefix, string postfix ) {
         Patch( patchedClass, patchedMethod, Public | Instance, new Type[]{ parameterType }, prefix, postfix );
      }

      protected void Patch ( Type patchedClass, string patchedMethod, Type[] parameterTypes, string prefix, string postfix ) {
         Patch( patchedClass, patchedMethod, Public | Instance, parameterTypes, prefix, postfix );
      }

      protected void Patch ( Type patchedClass, string patchedMethod, BindingFlags flags, string prefix, string postfix ) {
         Patch( patchedClass, patchedMethod, flags, (Type[]) null, prefix, postfix );
      }

      protected void Patch ( Type patchedClass, string patchedMethod, BindingFlags flags, Type parameterType, string prefix, string postfix ) {
         Patch( patchedClass, patchedMethod, flags, new Type[]{ parameterType }, prefix, postfix );
      }

      protected void Patch ( Type patchedClass, string patchedMethod, BindingFlags flags, Type[] parameterTypes, string prefix, string postfix ) {
         if ( ( flags & ( Static | Instance  ) ) == 0  ) flags |= Instance;
         if ( ( flags & ( Public | NonPublic ) ) == 0  ) flags |= Public;
         MethodInfo patched = null;
         Exception ex = null;
         try {
            if ( parameterTypes == null )
               patched = patchedClass.GetMethod( patchedMethod, flags );
            else
               patched = patchedClass.GetMethod( patchedMethod, flags, null, parameterTypes, null );
         } catch ( Exception e ) { ex = e; }
         if ( patched == null ) {
            Log.Error( "Cannot find {0}.{1}(...) to patch {2}", patchedClass.Name, patchedMethod, ex );
            return;
         }
         Patch( patched, prefix, postfix );
      }

      protected void Patch ( MethodBase patched, string prefix, string postfix ) {
         HarmonyMethod pre = MakePatch( prefix ), post = MakePatch( postfix );
         if ( pre == null && post == null ) return; // MakePatch would have reported method not found
         Patch( patched, pre, post );
      }

      protected void Patch ( MethodBase patched, MethodInfo prefix, MethodInfo postfix ) {
         Patch( patched, new HarmonyMethod( prefix ), new HarmonyMethod( postfix ) );
      }

      protected void Patch ( MethodBase patched, HarmonyMethod prefix, HarmonyMethod postfix ) {
         string pre = prefix?.method?.Name, post = postfix?.method?.Name;
         if ( patched == null ) {
            Log.Error( "Method not found. Cannot patch [ {0} : {1} ]", pre, post );
            return;
         }
         if ( Mod.ModHarmony == null ) {
            Mod.ModHarmony = HarmonyInstance.Create( Id );
            Log.Info( "Harmony instance \"{0}\"", Id );
         }
         Mod.ModHarmony.Patch( patched, prefix, postfix );
         Log.Verbo( "Patched: {0} {1} [ {2} : {3} ]", patched.DeclaringType, patched, pre, post );
      }

      // ============ UTILS ============
         
      public static string UppercaseFirst ( string s ) {
         if ( string.IsNullOrEmpty( s ) ) return string.Empty;
         return char.ToUpper( s[ 0 ] ) + s.Substring( 1 );
      }

      public static string ReplaceFirst ( string text, string search, string replace ) {
         int pos = text.IndexOf( search );
         if ( pos < 0 ) return text;
         int tLen = text.Length, sLen = search.Length, sEnd = pos + sLen;
         return new StringBuilder( tLen - sLen + replace.Length )
            .Append( text, 0, pos ).Append( replace ).Append( text, sEnd, tLen - sEnd )
            .ToString();
      }

      public static string Join<T> ( string separator, IEnumerable<T> list, Func<T,string> formatter = null ) {
         if ( list == null ) return string.Empty;
         StringBuilder result = new StringBuilder();
         foreach ( T e in list ) {
            if ( result.Length > 0 ) result.Append( separator );
            result.Append( formatter == null ? e?.ToString() : formatter( e ) );
         }
         return result.ToString();
      }

      public static string NullIfEmpty ( ref string value ) {
         if ( value == null ) return null;
         if ( value.Trim().Length <= 0 ) return value = null;
         return value;
      }

      public static void TryRun ( Action action ) { TryRun( BattleMod.BTML_LOG, action ); }
      public static void TryRun ( Logger log, Action action ) { try {
         action.Invoke();
      } catch ( Exception ex ) { log.Error( ex ); } }

      public static T TryGet<T> ( T[] array, int index, T fallback = default(T), string errorArrayName = null ) {
         if ( array == null || array.Length <= index ) {
            if ( errorArrayName != null ) BattleMod.BTML_LOG.Warn( $"{errorArrayName}[{index}] not found, using default {fallback}." );
            return fallback;
         }
         return array[ index ];
      }

      public static V TryGet<T,V> ( Dictionary<T, V> map, T key, V fallback = default(V), string errorDictName = null ) {
         if ( map == null || ! map.ContainsKey( key ) ) {
            if ( errorDictName != null ) BattleMod.BTML_LOG.Warn( $"{errorDictName}[{key}] not found, using default {fallback}." );
            return fallback;
         }
         return map[ key ];
      }

      public static T ValueCheck<T> ( ref T value, T fallback = default(T), Func<T,bool> validate = null ) {
         if ( value == null ) value = fallback;
         else if ( validate != null && ! validate( value ) ) value = fallback;
         return value;
      }

      public static int RangeCheck ( string name, ref int val, int min, int max ) {
         decimal v = val;
         RangeCheck( name, ref v, min, min, max, max );
         return val = (int) Math.Round( v );
      }

      public static decimal RangeCheck ( string name, ref decimal val, decimal min, decimal max ) {
         return RangeCheck( name, ref val, min, min, max, max );
      }

      public static decimal RangeCheck ( string name, ref decimal val, decimal shownMin, decimal realMin, decimal realMax, decimal shownMax ) {
         if ( realMin > realMax || shownMin > shownMax )
            BattleMod.BTML_LOG.Error( "Incorrect range check params on " + name );
         decimal orig = val;
         if ( val < realMin )
            val = realMin;
         else if ( val > realMax )
            val = realMax;
         if ( orig < shownMin && orig > shownMax ) {
            string message = "Warning: " + name + " must be ";
            if ( shownMin > decimal.MinValue )
               if ( shownMax < decimal.MaxValue )
                  message += " between " + shownMin + " and " + shownMax;
               else
                  message += " >= " + shownMin;
            else
               message += " <= " + shownMin;
            BattleMod.BTML_LOG.Info( message + ". Setting to " + val );
         }
         return val;
      }
   }

   //
   // JSON serialisation
   //

   [ AttributeUsage( AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false ) ]
   public class JsonSection : Attribute {
      public string Section;
      public JsonSection ( string section ) { Section = section ?? string.Empty; }
   }

   [ AttributeUsage( AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false ) ]
   public class JsonComment : Attribute {
      public string[] Comments;
      public JsonComment ( string comment ) { Comments = comment?.Split( '\n' ) ?? new string[]{ string.Empty }; }
      public JsonComment ( string[] comments ) { Comments = comments ?? new string[]{}; }
   }

   public class BattleJsonContract : DefaultContractResolver {
      protected override List<MemberInfo> GetSerializableMembers ( Type type ) {
         return base.GetSerializableMembers( type ).Where( ( member ) =>
            member.GetCustomAttributes( typeof( ObsoleteAttribute ), true ).Length <= 0
         ).ToList();
      }

      private static readonly string Indent = "  ";
      public static string FormatSettingJsonText ( Type type, string text ) {
         string NewLine = text.Contains( "\r\n" ) ? "\r\n" : "\n";
         string NewIndent = NewLine + Indent;
         foreach ( MemberInfo member in type.GetMembers() ) {
            if ( ( member.MemberType | MemberTypes.Field | MemberTypes.Property ) == 0 ) continue;
            object[] sections = member.GetCustomAttributes( typeof( JsonSection ), true );
            object[] comments = member.GetCustomAttributes( typeof( JsonComment ), true );
            if ( sections.Length <= 0 && comments.Length <= 0 ) continue;
            string propName = NewLine + Indent + JsonConvert.ToString( member.Name );
            string injection = "";
            if ( sections.Length > 0 )
               injection += NewLine +
                            NewIndent + "//" +
                            NewIndent + "// " + ( sections[0] as JsonSection )?.Section +
                            NewIndent + "//" + NewLine +
                            NewLine;
            if ( comments.Length > 0 ) {
               string[] lines = ( comments[0] as JsonComment )?.Comments;
               // Insert blank line if not new section
               if ( sections.Length <= 0 )
                  injection += NewLine + NewLine;
               // Actual property comment
               if ( lines.Length > 1 ) {
                  injection += Indent + "/* " + lines[0];
                  for ( int i = 1, len = lines.Length ; i < len ; i++ )
                     injection += NewIndent + " * " + lines[i];
                  injection += " */";
               } else if ( lines.Length > 0 )
                  injection += Indent + "/* " + lines[0] + " */";
               injection += NewLine;
            }
            text = BattleModModule.ReplaceFirst( text, propName, injection + propName );
         }
         return text;
      }
   }
}