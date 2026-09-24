using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Live output of running shell commands (Bash / PowerShell tool) for the tool cards.
    /// <para>
    /// The CLI's stream stays silent while a command runs — the output only arrives with the
    /// <c>tool_result</c> at the end. But the CLI itself writes the output live to
    /// <c>%TEMP%\claude\&lt;munged cwd&gt;\&lt;session id&gt;\tasks\&lt;task id&gt;.output</c> and announces
    /// the task with <c>system/task_started</c> (carrying the <c>tool_use_id</c> of the card). This
    /// class tails that file and reports new text per tool_use_id. Measured against CLI 2.1.280
    /// (2026-09-23): the file grows line by line while the command runs and is deleted when it ends.
    /// </para>
    /// <para>
    /// Undocumented CLI internals, so strictly best-effort: if the file never shows up (layout
    /// changed, different temp root), nothing is reported and the card behaves exactly as before.
    /// Purely local — it only reads a file the official binary writes; no network, no token.
    /// </para>
    /// </summary>
    public sealed class TaskOutputWatcher : IDisposable
    {
        /// <summary>How often running tasks are polled.</summary>
        public const int DefaultPollMs = 300;

        /// <summary>Give up on a task whose file never appeared within this time.</summary>
        private static readonly TimeSpan FileWaitTimeout = TimeSpan.FromSeconds(15);

        /// <summary>Re-run the (directory-enumerating) fallback search at most this often.</summary>
        private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(1);

        /// <summary>Most bytes read per task per poll — a chatty command cannot stall the poll loop.</summary>
        private const int MaxReadPerPoll = 64 * 1024;

        /// <summary>When the reader falls this far behind, skip ahead instead of replaying everything.</summary>
        private const long MaxBacklog = 512 * 1024;

        private readonly Action<string, string> _onOutput;
        private readonly IReadOnlyList<string> _tempRoots;
        private readonly int _pollMs;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Tail> _tails = new Dictionary<string, Tail>(StringComparer.Ordinal);
        private Timer? _timer;
        private int _polling;
        private bool _disposed;

        /// <param name="onOutput">Called with (tool_use_id, new text) from a background thread.</param>
        /// <param name="tempRoots">The CLI's temp roots (<c>…\claude</c>); defaults to the user's temp dir.</param>
        /// <param name="pollMs">Poll interval; 0 = no timer (tests drive <see cref="Poll"/> directly).</param>
        public TaskOutputWatcher(Action<string, string> onOutput, IReadOnlyList<string>? tempRoots = null, int pollMs = DefaultPollMs)
        {
            _onOutput = onOutput ?? throw new ArgumentNullException(nameof(onOutput));
            _tempRoots = tempRoots ?? DefaultTempRoots();
            _pollMs = pollMs;
        }

        /// <summary><c>%TEMP%\claude</c> — the directory the CLI keeps its per-session task files in.</summary>
        public static IReadOnlyList<string> DefaultTempRoots()
        {
            var roots = new List<string>();
            try { roots.Add(Path.Combine(Path.GetTempPath(), "claude")); } catch { }
            return roots;
        }

        /// <summary>Expected file for a task: <c>&lt;root&gt;\&lt;munged cwd&gt;\&lt;session&gt;\tasks\&lt;task&gt;.output</c>.</summary>
        public static string BuildPath(string tempRoot, string cwd, string sessionId, string taskId) =>
            Path.Combine(tempRoot, CliSessionReader.MungePath(cwd), sessionId, "tasks", taskId + ".output");

        /// <summary>Number of tasks currently being tailed (tests/diagnostics).</summary>
        public int ActiveCount
        {
            get { lock (_gate) return _tails.Count; }
        }

        /// <summary>Starts tailing a task's output file. No-op for missing ids or a task already tailed.</summary>
        public void Start(string taskId, string toolUseId, string? sessionId, string? cwd)
        {
            if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(toolUseId) || string.IsNullOrEmpty(sessionId))
                return;

            lock (_gate)
            {
                if (_disposed || _tails.ContainsKey(taskId))
                    return;

                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(cwd))
                {
                    foreach (var root in _tempRoots)
                        candidates.Add(BuildPath(root, cwd!, sessionId!, taskId));
                }

                _tails[taskId] = new Tail(taskId, toolUseId, sessionId!, candidates, DateTime.UtcNow);
                if (_pollMs > 0 && _timer == null)
                    _timer = new Timer(_ => Poll(), null, _pollMs, _pollMs);
            }
        }

        /// <summary>Reads what is left of a task, reports it, and stops tailing it.</summary>
        public void Stop(string taskId)
        {
            Tail? tail;
            lock (_gate)
            {
                if (!_tails.TryGetValue(taskId, out tail))
                    return;
                _tails.Remove(taskId);
                StopTimerIfIdle();
            }
            Drain(tail, finalRead: true);
        }

        /// <summary>Same as <see cref="Stop"/>, addressed by the tool_use the task belongs to.</summary>
        public void StopByToolUse(string toolUseId)
        {
            string? taskId;
            lock (_gate)
                taskId = _tails.Values.FirstOrDefault(t => t.ToolUseId == toolUseId)?.TaskId;
            if (taskId != null)
                Stop(taskId);
        }

        /// <summary>Stops every tail without a final read (turn ended / cancelled).</summary>
        public void StopAll()
        {
            lock (_gate)
            {
                _tails.Clear();
                StopTimerIfIdle();
            }
        }

        /// <summary>One poll over every running task. Public so tests can drive it without a timer.</summary>
        public void Poll()
        {
            if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
                return; // previous poll still running (slow disk) — skip this tick
            try
            {
                Tail[] tails;
                lock (_gate)
                    tails = _tails.Values.ToArray();

                foreach (var tail in tails)
                {
                    var finished = Drain(tail, finalRead: false);
                    if (finished)
                    {
                        lock (_gate)
                        {
                            _tails.Remove(tail.TaskId);
                            StopTimerIfIdle();
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        /// <summary>Reads new bytes of one task and reports them. Returns true when the task is over
        /// (its file was deleted after being seen, or it never appeared in time).</summary>
        private bool Drain(Tail tail, bool finalRead)
        {
            lock (tail)
            {
                try
                {
                    if (tail.Path == null && !Resolve(tail, force: finalRead))
                        return DateTime.UtcNow - tail.StartedUtc > FileWaitTimeout;

                    if (!File.Exists(tail.Path))
                        return tail.SeenFile; // the CLI deletes the file when the command ends

                    tail.SeenFile = true;
                    using var stream = new FileStream(
                        tail.Path!, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    var length = stream.Length;
                    if (length < tail.Offset)
                    {
                        tail.Offset = 0; // file was truncated/recreated — start over
                        tail.Decoder.Reset();
                    }

                    var text = new StringBuilder();
                    if (length - tail.Offset > MaxBacklog)
                    {
                        // Far behind (huge output): skip to the recent tail instead of replaying megabytes.
                        tail.Offset = length - MaxReadPerPoll;
                        tail.Decoder.Reset();
                        text.Append("\n… (earlier output skipped) …\n");
                    }

                    var budget = finalRead ? MaxBacklog : MaxReadPerPoll;
                    var buffer = new byte[8192];
                    stream.Seek(tail.Offset, SeekOrigin.Begin);
                    while (budget > 0)
                    {
                        var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, budget));
                        if (read <= 0)
                            break;
                        tail.Offset += read;
                        budget -= read;
                        var chars = new char[tail.Decoder.GetCharCount(buffer, 0, read)];
                        tail.Decoder.GetChars(buffer, 0, read, chars, 0);
                        text.Append(chars);
                    }

                    if (text.Length > 0)
                        _onOutput(tail.ToolUseId, text.ToString());
                    return false;
                }
                catch (Exception)
                {
                    // Locked/vanished between Exists and Open, access denied … — try again next tick.
                    return false;
                }
            }
        }

        /// <summary>Finds the task file: the expected path first, then any
        /// <c>&lt;root&gt;\*\&lt;session&gt;\tasks\&lt;task&gt;.output</c> in case the CLI munges the cwd
        /// differently than we do.</summary>
        private bool Resolve(Tail tail, bool force)
        {
            foreach (var candidate in tail.Candidates)
            {
                if (File.Exists(candidate))
                {
                    tail.Path = candidate;
                    return true;
                }
            }

            var now = DateTime.UtcNow;
            if (!force && now - tail.LastSearchUtc < SearchInterval)
                return false;
            tail.LastSearchUtc = now;

            foreach (var root in _tempRoots)
            {
                try
                {
                    if (!Directory.Exists(root))
                        continue;
                    foreach (var projectDir in Directory.EnumerateDirectories(root))
                    {
                        var path = Path.Combine(projectDir, tail.SessionId, "tasks", tail.TaskId + ".output");
                        if (File.Exists(path))
                        {
                            tail.Path = path;
                            return true;
                        }
                    }
                }
                catch
                {
                    // unreadable root — keep looking elsewhere
                }
            }
            return false;
        }

        private void StopTimerIfIdle()
        {
            if (_tails.Count > 0 || _timer == null)
                return;
            _timer.Dispose();
            _timer = null;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _tails.Clear();
                _timer?.Dispose();
                _timer = null;
            }
        }

        private sealed class Tail
        {
            public Tail(string taskId, string toolUseId, string sessionId, List<string> candidates, DateTime startedUtc)
            {
                TaskId = taskId;
                ToolUseId = toolUseId;
                SessionId = sessionId;
                Candidates = candidates;
                StartedUtc = startedUtc;
            }

            public string TaskId { get; }
            public string ToolUseId { get; }
            public string SessionId { get; }
            public List<string> Candidates { get; }
            public DateTime StartedUtc { get; }
            public DateTime LastSearchUtc { get; set; }
            public string? Path { get; set; }
            public long Offset { get; set; }
            public bool SeenFile { get; set; }
            /// <summary>Stateful, so a multi-byte UTF-8 character split across two reads decodes intact.</summary>
            public Decoder Decoder { get; } = new UTF8Encoding(false).GetDecoder();
        }
    }
}
