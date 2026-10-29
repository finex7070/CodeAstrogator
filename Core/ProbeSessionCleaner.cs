using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Removes the session files our own status probes left in the CLI's store. Every <c>claude -p /usage</c>
    /// poll (once a minute) and every <c>claude -p /model</c> probe is a regular CLI session, and before
    /// <c>--no-session-persistence</c> was passed each wrote a <c>&lt;id&gt;.jsonl</c> into
    /// <c>~\.claude\projects\&lt;munged cwd&gt;</c> — tens of thousands per user. Only files that are nothing
    /// but such a probe are deleted (see <see cref="IsProbeTranscript"/>); anything else is never touched.
    /// Best-effort, off the UI thread.
    /// </summary>
    public static class ProbeSessionCleaner
    {
        /// <summary>A probe transcript is ~4 KB; anything much bigger is a real conversation.</summary>
        internal const long MaxProbeFileBytes = 16 * 1024;

        /// <summary>Files younger than this may belong to a probe that is still running.</summary>
        internal static readonly TimeSpan MinAge = TimeSpan.FromMinutes(10);

        private static readonly HashSet<string> ProbeCommands = new HashSet<string>(StringComparer.Ordinal) { "/usage", "/model" };

        /// <summary>Deletes the probe transcripts under <paramref name="projectsRoot"/> (all project folders).
        /// Returns how many files were removed.</summary>
        public static int Clean(string? projectsRoot = null, CancellationToken ct = default)
        {
            var root = projectsRoot ?? CliSessionReader.DefaultProjectsRoot;
            var removed = 0;
            try
            {
                if (!Directory.Exists(root))
                    return 0;
                var cutoff = DateTime.UtcNow - MinAge;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl"))
                    {
                        if (ct.IsCancellationRequested)
                            return removed;
                        try
                        {
                            var info = new FileInfo(path);
                            if (info.Length > MaxProbeFileBytes || info.LastWriteTimeUtc > cutoff)
                                continue;
                            if (!IsProbeTranscript(File.ReadAllLines(path)))
                                continue;
                            File.Delete(path);
                            removed++;
                        }
                        catch
                        {
                            // locked / gone / unreadable — leave it
                        }
                    }
                }
            }
            catch
            {
                // best-effort
            }
            return removed;
        }

        /// <summary>
        /// True when the transcript is exactly one headless slash-command probe: it starts with the queue
        /// entry of <c>/usage</c> or <c>/model</c> (how <c>-p /cmd</c> sessions begin, CLI 2.1.1xx–2.1.312),
        /// holds no assistant message and no user message other than the CLI's command tags. A conversation in
        /// which someone typed <c>/usage</c> and then went on has an assistant or a typed user message and is
        /// kept; so is anything that does not parse.
        /// </summary>
        internal static bool IsProbeTranscript(IReadOnlyList<string> lines)
        {
            if (lines.Count == 0)
                return false;
            try
            {
                var first = JObject.Parse(lines[0]);
                if (first.Value<string>("type") != "queue-operation"
                    || first.Value<string>("operation") != "enqueue"
                    || !ProbeCommands.Contains(first.Value<string>("content") ?? ""))
                    return false;

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    var obj = JObject.Parse(line);
                    var type = obj.Value<string>("type");
                    if (type == "assistant")
                        return false;
                    if (type == "queue-operation" && obj["content"] is JValue queued
                        && !ProbeCommands.Contains(queued.Value<string>() ?? ""))
                        return false; // a second, real prompt was queued
                    if (type == "user")
                    {
                        if (!(obj["message"]?["content"] is JValue content))
                            return false; // tool results / blocks — a real turn
                        var text = (content.Value<string>() ?? "").TrimStart();
                        if (!text.StartsWith("<", StringComparison.Ordinal))
                            return false; // typed by a person
                    }
                }
                return true;
            }
            catch
            {
                return false; // never delete on ambiguity
            }
        }
    }
}
