using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace MoodyBlues
{
    /// <summary>Severity of a recorded log. Values are the 2-bit level field of the Log event.</summary>
    public enum LogLevel : byte
    {
        Info = 1,
        Warning = 2,
        Error = 3,
    }

    /// <summary>
    /// Colour of a recorded log in the replay viewer. Values 0-15 are the 6-bit colour field of
    /// the Log event; 16-63 are reserved and shown as gray. <see cref="Default"/> is never sent:
    /// it resolves to Gray for Info, Yellow for Warning and Red for Error.
    /// </summary>
    public enum LogColor : byte
    {
        Gray = 0,
        White = 1,
        Red = 2,
        Orange = 3,
        Yellow = 4,
        Lime = 5,
        Green = 6,
        Teal = 7,
        Cyan = 8,
        Sky = 9,
        Blue = 10,
        Indigo = 11,
        Violet = 12,
        Pink = 13,
        Rose = 14,
        Brown = 15,

        Default = 255,
    }

    /// <summary>
    /// Writes developer logs into the current recording (Log event, Spec.md Section 6.7). These are
    /// the only events the replay viewer lists in its session panel. Unity's Debug.Log output is
    /// not forwarded.
    ///
    /// Logs written while nothing is recording are dropped. Logs written while a recording is
    /// starting (StartRecording called, handshake not finished) are held and sent right after the
    /// start-of-recording snapshot, stamped with its time. Safe to call from any thread; calls off
    /// the main thread are queued and sent on the next frame.
    /// </summary>
    public static class MoodyBluesLog
    {
        /// <summary>Longest message kept, in UTF-8 bytes. Longer messages are truncated.</summary>
        public const int MaxMessageBytes = 16 * 1024;

        private const int MaxQueuedOffThread = 1024;

        private static int _mainThreadId = -1;
        private static readonly ConcurrentQueue<(LogLevel Level, LogColor Color, string Message)> OffThreadQueue = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void CaptureMainThread()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            while (OffThreadQueue.TryDequeue(out _)) { }
        }

        public static void Info(string message, LogColor color = LogColor.Default) =>
            Write(LogLevel.Info, message, color);

        public static void Warning(string message, LogColor color = LogColor.Default) =>
            Write(LogLevel.Warning, message, color);

        public static void Error(string message, LogColor color = LogColor.Default) =>
            Write(LogLevel.Error, message, color);

        public static void Write(LogLevel level, string message, LogColor color = LogColor.Default)
        {
            if (level != LogLevel.Info && level != LogLevel.Warning && level != LogLevel.Error) level = LogLevel.Info;
            message ??= string.Empty;

            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                // Unity objects can only be touched from the main thread, so the session manager
                // drains this from LateUpdate. Bounded so a background thread logging with nothing
                // recording cannot grow it forever.
                if (OffThreadQueue.Count < MaxQueuedOffThread) OffThreadQueue.Enqueue((level, color, message));
                return;
            }

            MoodyBluesSessionManager session = MoodyBluesSessionManager.Instance;
            if (session != null) session.EnqueueLog(level, ResolveColor(level, color), message);
        }

        /// <summary>Resolves <see cref="LogColor.Default"/> and out-of-range values to a sendable colour.</summary>
        public static LogColor ResolveColor(LogLevel level, LogColor color)
        {
            if (color != LogColor.Default && (byte)color < 64) return color;
            return level switch
            {
                LogLevel.Warning => LogColor.Yellow,
                LogLevel.Error => LogColor.Red,
                _ => LogColor.Gray,
            };
        }

        /// <summary>Style byte of a log's first event: level in the top 2 bits, colour in the low 6.</summary>
        public static byte PackStyle(LogLevel level, LogColor color) =>
            (byte)(((byte)level << 6) | ((byte)color & 0x3F));

        /// <summary>Called once per frame by MoodyBluesSessionManager on the main thread.</summary>
        internal static void DrainOffThreadQueue(MoodyBluesSessionManager session)
        {
            while (OffThreadQueue.TryDequeue(out var entry))
            {
                session.EnqueueLog(entry.Level, ResolveColor(entry.Level, entry.Color), entry.Message);
            }
        }
    }
}
