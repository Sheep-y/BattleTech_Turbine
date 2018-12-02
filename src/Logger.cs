using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Sheepy.Logging {
   public class Logger : IDisposable {
      public Logger ( string file, string blockDelete ) : this( file, 1000, blockDelete ) { }
      public Logger ( string file, int writeDelay = 1000, string blockDelete = null ) {
         if ( string.IsNullOrEmpty( file ) ) throw new NullReferenceException();
         BlockDeleteReason = blockDelete;
         LogFile = file.Trim();
         if ( writeDelay < 0 ) return;
         this.writeDelay = writeDelay;
         queue = new List<LogEntry>();
         worker = new Thread( WorkerLoop ) { Name = "Logger " + LogFile, Priority = ThreadPriority.BelowNormal };
         worker.Start();
      }

      // ============ Self Prop ============

      private readonly string BlockDeleteReason; // Non-null to block delete

      protected Func<SourceLevels,string> _LevelText = ( level ) => { //return level.ToString() + ": ";
         if ( level <= SourceLevels.Critical ) return "CRIT "; if ( level <= SourceLevels.Error       ) return "ERR  ";
         if ( level <= SourceLevels.Warning  ) return "WARN "; if ( level <= SourceLevels.Information ) return "INFO ";
         if ( level <= SourceLevels.Verbose  ) return "FINE "; return "TRAC ";
      };
      protected string _TimeFormat = "hh:mm:ss.ffff ", _Prefix = null, _Postfix = null;
      protected List<Func<LogEntry,bool>> _Filters = null;
      protected bool _IgnoreDuplicateExceptions = true;
      protected Action<Exception> _OnError = ( ex ) => Console.Error.WriteLine( ex );

      public class LogEntry { public DateTime time; public SourceLevels level; public object message; public object[] args; }

      // Worker states locked by queue which is private.
      private HashSet<string> exceptions = new HashSet<string>(); // Double as public get/set lock object
      private readonly List<LogEntry> queue;
      private Thread worker;
      private int writeDelay;

      // ============ Public Prop ============

      public static string Stacktrace { get { return new StackTrace( true ).ToString(); } }
      public string LogFile { get; private set; }

      public volatile SourceLevels LogLevel = SourceLevels.Information;
      // Time format, placed at the beginning of every line.
      public string TimeFormat {
         get { lock( exceptions ) { return _TimeFormat; } }
         set { lock( exceptions ) { _TimeFormat = value; } } }
      // Level format, placed between time and line.
      public Func<SourceLevels,string> LevelText {
         get { lock( exceptions ) { return _LevelText; } }
         set { lock( exceptions ) { _LevelText = value; } } }
      // String to add to the start of every line on write (not on log).
      public string Prefix {
         get { lock( exceptions ) { return _Prefix; } }
         set { lock( exceptions ) { _Prefix = value; } } }
      // String to add to the end of every line on write (not on log).
      public string Postfix {
         get { lock( exceptions ) { return _Postfix; } }
         set { lock( exceptions ) { _Postfix = value; } } }
      // Ignores duplicate exception logging (the exception must be the primary logged object, not as a parameter).
      public bool IgnoreDuplicateExceptions {
         get { lock( exceptions ) { return _IgnoreDuplicateExceptions; } }
         set { lock( exceptions ) { _IgnoreDuplicateExceptions = value; } } }
      // Handles "environmental" errors such as unable to write or delete log. Does not handle logical errors like log after dispose.
      public Action<Exception> OnError {
         get { lock( exceptions ) { return _OnError; } }
         set { lock( exceptions ) { _OnError = value; } } }

      // ============ API ============

      public virtual bool Exists () { return File.Exists( LogFile ); }

      public virtual void Delete () {
         if ( BlockDeleteReason != null ) {
            HandleError( new ApplicationException( "Cannot delete " + LogFile + ": " + BlockDeleteReason ) );
            return;
         }
         try {
            File.Delete( LogFile );
         } catch ( Exception e ) { HandleError( e ); }
      }

      public void Log ( SourceLevels level, object message, params object[] args ) {
         if ( ( level & LogLevel ) != level ) return;
         LogEntry entry = new LogEntry(){ time = DateTime.Now, level = level, message = message, args = args };
         if ( queue != null ) lock ( queue ) {
            if ( worker == null ) throw new InvalidOperationException( "Logger already disposed." );
            queue.Add( entry );
            Monitor.Pulse( queue );
         } else lock ( queue ) {
            OutputLog( _Filters, entry );
         }
      }

      // Each filter may modify the line, and may return false to exclude an input line from logging.
      // First input is unformatted log line, second input is log entry.
      public bool AddFilter ( Func<LogEntry,bool> filter ) { lock( queue ) {
         if ( filter == null ) return false;
         if ( _Filters == null ) _Filters = new List<Func<LogEntry,bool>>();
         else if ( _Filters.Contains( filter ) ) return false;
         _Filters.Add( filter );
         return true;
      } }

      public bool RemoveFilter ( Func<LogEntry,bool> filter ) { lock( queue ) {
         if ( filter == null || _Filters == null ) return false;
         bool result = _Filters.Remove( filter );
         if ( result && _Filters.Count <= 0 ) _Filters = null;
         return result;
      } }

      public void Trace ( object message = null, params object[] args ) { Log( SourceLevels.ActivityTracing, message, args ); }
      public void Verbo ( object message = null, params object[] args ) { Log( SourceLevels.Verbose, message, args ); }
      public void Info  ( object message = null, params object[] args ) { Log( SourceLevels.Information, message, args ); }
      public void Warn  ( object message = null, params object[] args ) { Log( SourceLevels.Warning, message, args ); }
      public void Error ( object message = null, params object[] args ) { Log( SourceLevels.Error, message, args ); }

      // ============ Implementations ============

      private void HandleError ( Exception ex ) { try {
         lock( exceptions ) { _OnError?.Invoke( ex ); }
      } catch ( Exception ) { } }

      private void WorkerLoop () {
         do {
            int delay = 0;
            lock ( queue ) {
               if ( worker == null ) return;
               try {
                  if ( queue.Count <= 0 ) Monitor.Wait( queue );
               } catch ( ThreadInterruptedException ) { }
               delay = writeDelay;
            }
            if ( delay > 0 )
               Thread.Sleep( writeDelay );
            Flush();
         } while ( true );
      }

      public bool? Flush () {
         Func<LogEntry,bool>[] filters;
         LogEntry[] entries;
         lock ( queue ) {
            filters = _Filters?.ToArray();
            entries = queue.ToArray();
            queue.Clear();
         }
         return OutputLog( filters, entries );
      }

      private bool? OutputLog ( IEnumerable<Func<LogEntry,bool>> filters, params LogEntry[] entries ) {
         if ( entries.Length <= 0 ) return null;
         StringBuilder buf = new StringBuilder();
         lock ( exceptions ) { // Not expecting settings to change frequently. Lock outside format loop for higher throughput.
            foreach ( LogEntry line in entries ) try {
               if ( filters != null ) foreach ( Func<LogEntry,bool> filter in filters ) try {
                  if ( ! filter( line ) ) continue;
               } catch ( Exception ) { }
               string txt = line.message?.ToString();
               if ( ! string.IsNullOrEmpty( txt ) ) {
                  if ( ! AllowMessagePass( line, txt ) ) continue;
                  FormatMessage( buf, line, txt );
               }
               NewLine( buf, line );
            } catch ( Exception ex ) {
               buf?.Append( Environment.NewLine ); // Clear error'ed line
               HandleError( ex );
            }
         }
         return OutputLog( buf );
      }

      // Override to control which message get logged.
      protected virtual bool AllowMessagePass ( LogEntry line, string txt ) {
         if ( line.message is Exception ex && IgnoreDuplicateExceptions ) {
            if ( exceptions.Contains( txt ) ) return false;
            exceptions.Add( txt );
         }
         return true;
      }

      // Override to change line/entry format.
      protected virtual void FormatMessage ( StringBuilder buf, LogEntry line, string txt ) {
         if ( ! string.IsNullOrEmpty( _TimeFormat ) )
            buf.Append( line.time.ToString( _TimeFormat ) );
         if ( _LevelText != null )
            buf.Append( _LevelText( line.level ) );
         buf.Append( _Prefix );
         if ( line.args != null && line.args.Length > 0 && txt != null ) try {
            txt = string.Format( txt, line.args );
         } catch ( FormatException ) {}
         buf.Append( txt ).Append( _Postfix );
      }

      // Called after every entry, even null or empty.
      protected virtual void NewLine ( StringBuilder buf, LogEntry line ) {
         buf.Append( Environment.NewLine );
      }

      // Override to change log output, e.g. to console, system event log, or development environment.
      protected virtual bool? OutputLog ( StringBuilder buf ) { try {
         if ( buf.Length <= 0 ) return null;
         File.AppendAllText( LogFile, buf.ToString() );
         return true;
      } catch ( Exception ex ) { HandleError( ex ); return false; } }

      public void Dispose () {
         if ( queue != null ) lock ( queue ) {
            worker = null;
            writeDelay = 0; // Flush log immediately
            Monitor.Pulse( queue );
         }
      }
   }
}