using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MusicBar
{
    /// <summary>
    /// A small, best-effort diagnostic sink. Use metadata only: never pass lyric text,
    /// provider response bodies, authorization headers, cookies, or credentials.
    /// </summary>
    public sealed class DiagnosticsLog : IDisposable
    {
        private const long DefaultSegmentBytes = 2L * 1024L * 1024L;
        private const int DefaultQueueCapacity = 2048;
        private static readonly object SharedLock = new object();
        private static DiagnosticsLog shared;
        private static readonly Regex SecretAssignment = new Regex(
            @"(?i)\b(authorization|proxy-authorization|set-cookie|cookie|access[_-]?token|refresh[_-]?token|token|secret|password)\b\s*[:=]\s*.*",
            RegexOptions.Compiled);
        private static readonly Regex SecretQuery = new Regex(
            @"(?i)([?&](?:access[_-]?token|refresh[_-]?token|token|secret|password)=)[^&#\s]+",
            RegexOptions.Compiled);

        private readonly string directory;
        private readonly long maxSegmentBytes;
        private readonly Func<DateTime> clock;
        private readonly BlockingCollection<LogItem> queue;
        private readonly object acceptanceLock = new object();
        private readonly Thread worker;
        private int accepting = 1;
        private int droppedCount;
        private int unreportedDroppedCount;
        private int writeFailed;
        private StreamWriter writer;
        private DateTime activeDate;
        private int activeSegment;
        private long activeBytes;

        /// <summary>Creates daily files under &lt;dataDirectory&gt;/logs.</summary>
        public DiagnosticsLog(string dataDirectory)
            : this(dataDirectory, null, DefaultSegmentBytes, DefaultQueueCapacity)
        {
        }

        internal DiagnosticsLog(string dataDirectory, Func<DateTime> localClock, long segmentBytes, int queueCapacity)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("A data directory is required.", "dataDirectory");
            if (segmentBytes < 256) throw new ArgumentOutOfRangeException("segmentBytes");
            if (queueCapacity < 1) throw new ArgumentOutOfRangeException("queueCapacity");
            directory = Path.Combine(dataDirectory, "logs");
            maxSegmentBytes = segmentBytes;
            clock = localClock ?? (() => DateTime.Now);
            queue = new BlockingCollection<LogItem>(new ConcurrentQueue<LogItem>(), queueCapacity);
            worker = new Thread(Consume) { IsBackground = true, Name = "MusicBar diagnostic log" };
            worker.Start();
        }

        /// <summary>Starts the process-wide diagnostic logger under data/logs.</summary>
        public static void Initialize(string dataDirectory)
        {
            DiagnosticsLog previous;
            lock (SharedLock)
            {
                previous = shared;
                shared = new DiagnosticsLog(dataDirectory);
            }
            if (previous != null) previous.Stop(TimeSpan.FromSeconds(2));
        }

        /// <summary>Queues a short, metadata-only event to the process-wide logger.</summary>
        public static bool Write(string category, string message)
        {
            DiagnosticsLog current;
            lock (SharedLock) current = shared;
            return current != null && current.Add(category, message);
        }

        /// <summary>Number of records dropped by the process-wide bounded queue.</summary>
        public static int DroppedRecords
        {
            get
            {
                DiagnosticsLog current;
                lock (SharedLock) current = shared;
                return current == null ? 0 : current.DroppedCount;
            }
        }

        /// <summary>Flushes and stops the process-wide logger with a bounded wait.</summary>
        public static bool Shutdown()
        {
            return Shutdown(TimeSpan.FromSeconds(2));
        }

        public static bool Shutdown(TimeSpan timeout)
        {
            DiagnosticsLog current;
            lock (SharedLock)
            {
                current = shared;
                shared = null;
            }
            return current == null || current.Stop(timeout);
        }

        /// <summary>Number of records rejected because the bounded queue was full or closed.</summary>
        public int DroppedCount { get { return Interlocked.CompareExchange(ref droppedCount, 0, 0); } }

        /// <summary>True if any disk operation has failed; logging failures never escape to the caller.</summary>
        public bool HadWriteFailure { get { return Interlocked.CompareExchange(ref writeFailed, 0, 0) != 0; } }

        /// <summary>
        /// Queues a short metadata record without waiting for disk I/O. Fields are line-safe,
        /// bounded, and common credential assignments are redacted before they enter the queue.
        /// </summary>
        public bool Add(string category, string message)
        {
            DateTime now;
            try { now = clock(); }
            catch { now = DateTime.Now; }
            var item = LogItem.Record(now, "信息", Clean(category, 128), Clean(message, 2048));
            lock (acceptanceLock)
            {
                if (accepting == 0)
                {
                    RecordDrop();
                    return false;
                }
                int unreported = Interlocked.Exchange(ref unreportedDroppedCount, 0);
                if (unreported > 0 && !queue.TryAdd(LogItem.Record(now, "警告", "日志队列丢弃",
                    "此前有 " + unreported.ToString(CultureInfo.InvariantCulture) + " 条事件因队列已满或日志已关闭而未写入。")))
                {
                    Interlocked.Add(ref unreportedDroppedCount, unreported);
                    RecordDrop();
                    return false;
                }
                if (!queue.TryAdd(item))
                {
                    RecordDrop();
                    return false;
                }
            }
            return true;
        }

        /// <summary>Waits at most the supplied duration for queued records to reach disk.</summary>
        public bool Flush(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
            using (var completed = new ManualResetEvent(false))
            {
                lock (acceptanceLock)
                {
                    if (accepting == 0) return !worker.IsAlive;
                    if (!queue.TryAdd(LogItem.Barrier(completed)))
                    {
                        return false;
                    }
                }
                bool signaled;
                try { signaled = completed.WaitOne(timeout); }
                catch { signaled = false; }
                return signaled && !HadWriteFailure;
            }
        }

        /// <summary>Stops accepting records and waits only up to the requested time to flush.</summary>
        internal bool Stop(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
            lock (acceptanceLock)
            {
                if (accepting != 0)
                {
                    accepting = 0;
                    queue.CompleteAdding();
                }
            }
            bool stopped;
            try { stopped = worker.Join(timeout); }
            catch { stopped = false; }
            return stopped && !HadWriteFailure;
        }

        public void Dispose()
        {
            Stop(TimeSpan.FromMilliseconds(1500));
        }

        private void Consume()
        {
            try
            {
                foreach (LogItem item in queue.GetConsumingEnumerable())
                {
                    if (item.Completion != null)
                    {
                        FlushWriter();
                        try { item.Completion.Set(); } catch { }
                    }
                    else
                    {
                        try { Append(item); }
                        catch { MarkWriteFailure(); CloseWriter(); }
                    }
                }
            }
            catch { MarkWriteFailure(); }
            finally
            {
                FlushWriter();
                CloseWriter();
                queue.Dispose();
            }
        }

        private void Append(LogItem item)
        {
            DateTime date = item.Timestamp.Date;
            if (writer == null || activeDate != date)
            {
                CloseWriter();
                OpenLatestSegment(date);
            }

            byte[] bytes = Encoding.UTF8.GetBytes(Format(item));
            if (activeBytes > 0 && activeBytes + bytes.Length > maxSegmentBytes)
            {
                CloseWriter();
                OpenSegment(date, activeSegment + 1, false);
            }
            writer.Write(Encoding.UTF8.GetString(bytes));
            writer.Flush();
            activeBytes += bytes.Length;
        }

        private void OpenLatestSegment(DateTime date)
        {
            Directory.CreateDirectory(directory);
            string prefix = "MusicBar-" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            int latest = -1;
            string[] files = Directory.GetFiles(directory, prefix + "*.log", SearchOption.TopDirectoryOnly);
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                int index;
                if (name.Equals(prefix + ".log", StringComparison.OrdinalIgnoreCase)) index = 0;
                else if (name.StartsWith(prefix + ".part", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(name.Substring(prefix.Length + 5, name.Length - prefix.Length - 9), NumberStyles.None, CultureInfo.InvariantCulture, out index))
                {
                    if (index > latest) latest = index;
                    continue;
                }
                else continue;
                if (index > latest) latest = index;
            }
            if (latest < 0) latest = 0;
            OpenSegment(date, latest, true);
            if (activeBytes >= maxSegmentBytes)
            {
                CloseWriter();
                OpenSegment(date, latest + 1, false);
            }
        }

        private void OpenSegment(DateTime date, int segment, bool append)
        {
            Directory.CreateDirectory(directory);
            string prefix = "MusicBar-" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string name = segment <= 0 ? prefix + ".log" : prefix + ".part" + segment.ToString("D2", CultureInfo.InvariantCulture) + ".log";
            string path = Path.Combine(directory, name);
            bool existing = File.Exists(path);
            var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            writer = new StreamWriter(stream, new UTF8Encoding(false));
            activeDate = date;
            activeSegment = segment;
            activeBytes = existing && append ? stream.Length : 0;
        }

        private void FlushWriter()
        {
            try { if (writer != null) writer.Flush(); }
            catch { MarkWriteFailure(); CloseWriter(); }
        }

        private void CloseWriter()
        {
            try { if (writer != null) writer.Dispose(); }
            catch { MarkWriteFailure(); }
            writer = null;
        }

        private void MarkWriteFailure()
        {
            Interlocked.Exchange(ref writeFailed, 1);
        }

        private void RecordDrop()
        {
            Interlocked.Increment(ref droppedCount);
            Interlocked.Increment(ref unreportedDroppedCount);
        }

        private static string Format(LogItem item)
        {
            return item.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "\t" +
                item.Level + "\t" + item.EventName + "\t" + item.Details + Environment.NewLine;
        }

        private static string Clean(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string clean = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            clean = SecretAssignment.Replace(clean, "$1=[已隐藏]");
            clean = SecretQuery.Replace(clean, "$1[已隐藏]");
            if (clean.Length > maxLength) clean = clean.Substring(0, maxLength);
            return clean;
        }

        private sealed class LogItem
        {
            internal DateTime Timestamp;
            internal string Level;
            internal string EventName;
            internal string Details;
            internal ManualResetEvent Completion;

            internal static LogItem Record(DateTime timestamp, string level, string eventName, string details)
            {
                return new LogItem { Timestamp = timestamp, Level = level, EventName = eventName, Details = details };
            }

            internal static LogItem Barrier(ManualResetEvent completion)
            {
                return new LogItem { Completion = completion };
            }
        }
    }
}
