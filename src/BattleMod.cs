using BattleTech.UI;
using BattleTech;
using Harmony;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text;
using System;
using UnityEngine;
using static System.Reflection.BindingFlags;

namespace Sheepy.BattleTechMod {

   public abstract class BattleMod : BattleModModule {

      // Basic mod info for public access, will auto load from assembly then mod.json (if exists)
      public string Version { get; protected set; } = "Unknown";

      protected BattleMod ( ) {
         SetupDefault();
         PatchBattleMods();
      }

      public void Start ( ref Logger log ) {
         CurrentMod = this;
         TryRun( Setup );
         log = Logger;
         Add( this );
         CurrentMod = null;
      }

      public void Start () {
         CurrentMod = this;
         TryRun( Setup );
         Add( this );
         CurrentMod = null;
      }

      public string BaseDir { get; protected set; }
      private string _LogDir;
      public string LogDir { 
         get { return _LogDir; }
         protected set {
            _LogDir = value;
            Logger = new Logger( GetLogFile() );
         }
      }
      public HarmonyInstance harmony { get; internal set; }

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

      // Fill in blanks with Assembly values
      private void SetupDefault () { TryRun( Logger, () => {
         Assembly file = GetType().Assembly;
         Id = GetType().Namespace;
         Name = file.GetName().Name;
         BaseDir = Path.GetDirectoryName( file.Location ) + "/"; 
         string mod_info_file = BaseDir + "mod.json";
         if ( File.Exists( mod_info_file ) ) TryRun( Logger, () => {
            ModInfo info = JsonConvert.DeserializeObject<ModInfo>( File.ReadAllText( mod_info_file ) );
            if ( ! string.IsNullOrEmpty( info.Name ) )
               Name = info.Name;
            if ( ! string.IsNullOrEmpty( info.Version ) )
               Version = info.Version;
         } );
         LogDir = BaseDir; // Create Logger after Name is read from mod.json
      } ); }

      // Override this method to override Namd and Id
      protected virtual void Setup () {
         Logger.Delete();
         Logger.Log( "{2} Loading {0} Version {1} In {3}" + Environment.NewLine, Name, Version, DateTime.Now.ToString( "s" ), BaseDir );
      }

      public static string Idify ( string text ) { return Join( string.Empty, new Regex( "\\W+" ).Split( text ), UppercaseFirst ); }

      protected virtual string GetLogFile () {
         return LogDir + "Log_" + Idify( Name ) + ".txt";
      }

      // Load settings from settings.json, call SanitizeSettings, and create/overwrite it if the content is different.
      protected virtual void LoadSettings <Settings> ( ref Settings settings, Func<Settings,Settings> sanitise = null ) {
         string file = BaseDir + "settings.json", fileText = String.Empty;
         Settings config = settings;
         if ( File.Exists( file ) ) TryRun( () => {
            fileText = File.ReadAllText( file );
            if ( fileText.Contains( "\"Name\"" ) && fileText.Contains( "\"DLL\"" ) && fileText.Contains( "\"Settings\"" ) ) TryRun( Logger, () => {
               JObject modInfo = JObject.Parse( fileText );
               if ( modInfo.TryGetValue( "Settings", out JToken embedded ) )
                  fileText = embedded.ToString( Formatting.None );
            } );
            config = JsonConvert.DeserializeObject<Settings>( fileText );
         } );
         if ( sanitise != null )
            TryRun( () => config = sanitise( config ) );
         string sanitised = JsonConvert.SerializeObject( config, Formatting.Indented, new JsonSerializerSettings { ContractResolver = new BattleJsonContract() } );
         Logger.Log( "Loaded Settings: " + sanitised );
         string commented = BattleJsonContract.FormatSettingJsonText( settings.GetType(), sanitised );
         if ( commented != fileText ) { // Can be triggered by comment or field update, not necessary sanitisation
            Logger.Log( "Updating " + file );
            SaveSettings( commented );
         }
         settings = config;
      }

      protected void SaveSettings ( Settings settings_object ) {
         SaveSettings( JsonConvert.SerializeObject( settings_object, Formatting.Indented ) );
      }

      private void SaveSettings ( string settings ) {
         TryRun( Logger, () => File.WriteAllText( BaseDir + "settings.json", settings ) );
      }

      // ============ Execution ============

      private static Dictionary<BattleMod, List<BattleModModule>> modules = new Dictionary<BattleMod, List<BattleModModule>>();

      public BattleMod Add ( BattleModModule module ) {
         if ( ! modules.TryGetValue( this, out List<BattleModModule> list ) )
            modules.Add( this, list = new List<BattleModModule>() );
         if ( ! list.Contains( module ) ) {
            if ( module != CurrentMod )
               if ( module.Id == this.Id ) module.Id += "." + Idify( module.Name );
            list.Add( module );
            TryRun( Logger, module.ModStarts );
         }
         return this;
      }

      private static bool BattleModsPatched = false;

      public void PatchBattleMods () {
         if ( BattleModsPatched ) return;
         Logger oldLog = this.Logger;
         this.Logger = Logger.BTML_LOG;
         LogPatch = false;
         Patch( typeof( UnityGameInstance ).GetMethod( "InitUserSettings", Instance | NonPublic ), null, typeof( BattleMod ).GetMethod( "RunGameStarts", Static | NonPublic ) );
         Patch( typeof( SimGameState ).GetMethod( "Init" ), null, typeof( BattleMod ).GetMethod( "RunCampaignStarts", Static | NonPublic ) );
         Patch( typeof( CombatHUD ).GetMethod( "Init", new Type[]{ typeof( CombatGameState ) } ), null, typeof( BattleMod ).GetMethod( "RunCombatStarts", Static | NonPublic ) );
         LogPatch = true;
         BattleModsPatched = true;
         this.Logger = oldLog;
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
      
      private static void CallAllModules ( Action<BattleModModule> task ) {
         foreach ( var mod in modules ) {
            foreach ( BattleModModule module in mod.Value ) try {
               task( module );
            } catch ( Exception ex ) { 
               mod.Key.Logger.Error( ex ); 
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
               Logger.BTML_LOG.Error( ex );
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
            Logger = Mod.Logger;
         }
      }

      public string Id { get; protected internal set; } = "org.example.mod.module";
      public string Name { get; protected internal set; } = "Module";
      
      private Logger _Logger;
      protected Logger Logger {
         get { return _Logger ?? Logger.BTML_LOG; }
         set { _Logger = value; }
      }

      // ============ Harmony ============

      /* Find and create a HarmonyMethod from this class. method must be public and has unique name. */
      protected HarmonyMethod MakePatch ( string method ) {
         if ( method == null ) return null;
         MethodInfo mi = GetType().GetMethod( method, Static | Public | NonPublic );
         if ( mi == null ) {
            Logger.Error( "Cannot find patch method " + method );
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
            Logger.Error( "Cannot find {0}.{1}(...) to patch {2}", patchedClass.Name, patchedMethod, ex );
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

      protected bool LogPatch = true; // TODO: Control with Log Level

      protected void Patch ( MethodBase patched, HarmonyMethod prefix, HarmonyMethod postfix ) {
         string pre = prefix?.method?.Name, post = postfix?.method?.Name;
         if ( patched == null ) {
            Logger.Error( "Method not found. Cannot patch [ {0} : {1} ]", pre, post );
            return;
         }
         if ( Mod.harmony == null )
            Mod.harmony = HarmonyInstance.Create( Id );
         Mod.harmony.Patch( patched, prefix, postfix );
         if ( LogPatch )
            Logger.Log( "Patched: {0} {1} [ {2} : {3} ]", patched.DeclaringType, patched, pre, post );
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

      public static string Join<T> ( string separator, T[] array, Func<T,string> formatter = null ) {
         if ( array == null ) return string.Empty;
         StringBuilder result = new StringBuilder();
         for ( int i = 0, len = array.Length ; i < len ; i++ ) {
            if ( i > 0 ) result.Append( separator );
            result.Append( formatter == null ? array[i]?.ToString() : formatter( array[i] ) );
         }
         return result.ToString();
      }

      public static string NullIfEmpty ( ref string value ) {
         if ( value == null ) return null;
         if ( value.Trim().Length <= 0 ) return value = null;
         return value;
      }

      public static void TryRun ( Action action ) { TryRun( Logger.BTML_LOG, action ); }
      public static void TryRun ( Logger log, Action action ) { try {
         action.Invoke();
      } catch ( Exception ex ) { log.Error( ex ); } }

      public static T TryGet<T> ( T[] array, int index, T fallback = default(T), string errorArrayName = null ) {
         if ( array == null || array.Length <= index ) {
            if ( errorArrayName != null ) Logger.BTML_LOG.Warn( $"{errorArrayName}[{index}] not found, using default {fallback}." );
            return fallback;
         }
         return array[ index ];
      }

      public static V TryGet<T,V> ( Dictionary<T, V> map, T key, V fallback = default(V), string errorDictName = null ) {
         if ( map == null || ! map.ContainsKey( key ) ) {
            if ( errorDictName != null ) Logger.BTML_LOG.Warn( $"{errorDictName}[{key}] not found, using default {fallback}." );
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
         float v = val;
         RangeCheck( name, ref v, min, min, max, max );
         return val = Mathf.RoundToInt( v );
      }

      public static float RangeCheck ( string name, ref float val, float min, float max ) {
         return RangeCheck( name, ref val, min, min, max, max );
      }

      public static float RangeCheck ( string name, ref float val, float shownMin, float realMin, float realMax, float shownMax ) {
         if ( realMin > realMax || shownMin > shownMax )
            Logger.BTML_LOG.Error( "Incorrect range check params on " + name );
         float orig = val;
         if ( val < realMin )
            val = realMin;
         else if ( val > realMax )
            val = realMax;
         if ( orig < shownMin && orig > shownMax ) {
            string message = "Warning: " + name + " must be ";
            if ( shownMin > float.MinValue )
               if ( shownMax < float.MaxValue )
                  message += " between " + shownMin + " and " + shownMax;
               else
                  message += " >= " + shownMin;
            else
               message += " <= " + shownMin;
            Logger.BTML_LOG.Log( message + ". Setting to " + val );
         }
         return val;
      }
   }

   //
   // Logging
   //

   public class Logger {

      public static readonly Logger BTML_LOG = new Logger( "Mods/BTModLoader.log" );
      public static readonly Logger BT_LOG = new Logger( "BattleTech_Data/output_log.txt" );

      public Logger ( string file ) {
         if ( String.IsNullOrEmpty( file ) ) throw new NullReferenceException();
         LogFile = file;
      }

      public string LogFile { get; private set; }

      public bool IgnoreDuplicateExceptions = true;
      public Dictionary<string, int> exceptions = new Dictionary<string, int>();

      public bool Exists () {
         return File.Exists( LogFile );
      }

      public Exception Delete () {
         if ( LogFile == "Mods/BTModLoader.log" || LogFile == "BattleTech_Data/output_log.txt" )
            return new ApplicationException( "Cannot delete BTModLoader.log or BattleTech game log." );

         Exception result = null;
         try {
            File.Delete( LogFile );
         } catch ( Exception e ) { result = e; }
         return result;
      }

      public void Log ( object message ) {
         string txt = message?.ToString();
         if ( message is Exception ex ) {
            if ( exceptions.ContainsKey( txt ) ) {
               exceptions[ txt ]++;
               if ( IgnoreDuplicateExceptions )
                  return;
            } else
               exceptions.Add( txt, 1 );
         }
         Log( txt ); 
      }
      public void Log ( string message, params object[] args ) { Log( Format( message, args ) ); }
      public void Log ( string message ) { WriteLog( message + NewLine ); }
      private static readonly string NewLine = Environment.NewLine;

      public void Warn ( object message ) { Warn( message?.ToString() ); }
      public void Warn ( string message ) { Log( "Warning: " + message ); }
      public void Warn ( string message, params object[] args ) {
         message = Format( message, args );
         //HBS.Logging.Logger.GetLogger( "Mods" ).LogWarning( "[AttackImprovementMod] " + message );
         Log( "Warning: " + message );
      }

      public bool Error ( object message ) { 
         if ( message is Exception )
            Log( message );
         else
            Error( message?.ToString() );
         return true;
      }
      public void Error ( string message ) { Log( "Error: " + message ); }
      public void Error ( string message, params object[] args ) {
         message = Format( message, args );
         Log( "Error: " + message ); 
      }

      protected void WriteLog ( string message ) {
         try {
            File.AppendAllText( LogFile, message );
         } catch ( Exception ex ) {
            Console.WriteLine( message );
            Console.Error.WriteLine( ex );
         }
      }

      protected static string Format ( string message, params object[] args ) {
         try {
            if ( args != null && args.Length > 0 )
               return string.Format( message, args );
         } catch ( Exception ) {}
         return message;
      }
   }

   //
   // JSON serialisation
   //

   [ AttributeUsage( AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false ) ]
   public class JsonSection : Attribute {
      public string Section;
      public JsonSection ( string section ) { Section = section ?? String.Empty; }
   }

   [ AttributeUsage( AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false ) ]
   public class JsonComment : Attribute {
      public string[] Comments;
      public JsonComment ( string comment ) { Comments = comment?.Split( '\n' ) ?? new string[]{ String.Empty }; }
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