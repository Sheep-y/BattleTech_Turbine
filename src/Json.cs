using System;
using System.Reflection;
using System.Text;

namespace Sheepy.BattleTechMod.Turbine {
   using System.Collections.Generic;
   using System.IO;
   using System.Text.RegularExpressions;
   using static Mod;
   using static System.Reflection.BindingFlags;

   public class JsonPatch : BattleModModule {

      public override void ModStarts () {
         Patch( typeof( HBS.Util.JSONSerializationUtility ), "StripHBSCommentsFromJSON", NonPublic | Static, "Override_StripComments", null );
      }

      private static int pos;

      /* Original code
		private static string StripHBSCommentsFromJSON ( string json ) {
         string str = string.Empty;
         string format = "{0}(.*?)\\{1}";
         foreach ( var keyValuePair in commentSurroundPairs ) {
            str = str + string.Format( format, keyValuePair.Key, keyValuePair.Value ) + "|";
         }
         string str2 = "\"((\\\\[^\\n]|[^\"\\n])*)\"|";
         string str3 = "@(\"[^\"]*\")+";
         string pattern = str + str2 + str3;
         return Regex.Replace( json, pattern, delegate ( Match me ) {
            foreach ( var keyValuePair2 in commentSurroundPairs ) {
               if ( me.Value.StartsWith( keyValuePair2.Key ) || me.Value.EndsWith( keyValuePair2.Value ) ) {
                  return string.Empty;
               }
            }
            return me.Value;
         }, RegexOptions.Singleline );
      }

      private static readonly Dictionary<string, string> commentSurroundPairs = new Dictionary<string, string> {
         { "//*", "*"+"/" },
         { "<!--", "-->" }
      }; */

      public static bool Override_StripComments ( ref String __result, String json ) { try {
         //Info( "=== Input ===\n{0}", json );
         __result = StripComments( json );
         /*
         string origResult = StripHBSCommentsFromJSON( json );
         if ( __result != origResult ) {
            Error( "Diff result. Data exported." );
            File.WriteAllText( "input.js", origResult );
            File.WriteAllText( "output.js", __result );
         }
         */
         return false;
      }                 catch ( Exception ex ) { return Error( ex ); } }

      public static String StripComments ( String json ) {
         pos = 0;
         StringBuilder buf = new StringBuilder( json.Length );
         do {
Loop:
            for ( int i = pos, len = json.Length - 2 ; i < len ; i++ ) {
               char a = json[ i ];
               if ( a == '/' ) { // Detect //* to */
                  char b = Peek( json, i+1 );
                  if ( b == '/' ) {
                     if ( Peek( json, i+2 ) == '*' ) { // //* to */
                        if ( SkipWS( buf, json, i, i+3, "*/" ) ) goto Loop;
                     } /*else {                          // Single line comment // to \n, conflict with url string and requires string state tracking
                        if ( Skip( buf, json, i, i+2, "\n" ) ) {
                           buf.Append( '\n' );
                           goto Loop;
                        }
                     }*/
                  } else if ( b == '*' ) { // /* to */
                     if ( SkipWS( buf, json, i, i+2, "*/" ) ) goto Loop;
                  }
               } else if ( a == '<' && Match( json, i+1, "!--" ) ) { // <!-- to -->
                  if ( SkipWS( buf, json, i, i+4, "-->" ) ) goto Loop;
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
      private static bool SkipWS ( StringBuilder buf, String json, int skipStart, int headEnd, String until ) {
         if ( ! Skip( buf, json, skipStart, headEnd, until ) ) return false;
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
      private static bool Skip ( StringBuilder buf, String json, int skipStart, int headEnd, String until ) {
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
   }
}