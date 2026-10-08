using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Builds the stdin line for <c>claude -p --input-format stream-json</c>: one user message,
    /// terminated by a newline. The process host closes stdin right after it — still one process per
    /// turn (the persistent bidirectional mode stays removed, see docs/NOTES.md).
    /// <para>
    /// Why stream-json instead of a plain-text prompt: the CLI emits <c>prompt_suggestion</c> only in
    /// this mode (plain text: generated but never surfaced — CLI 2.1.287), and it accepts content
    /// blocks such as images directly (<see cref="CliImageAttachments"/>). <c>@path</c> expansion and
    /// slash commands work the same.
    /// </para>
    /// </summary>
    public static class StreamJsonInput
    {
        /// <summary>
        /// Serializes <paramref name="prompt"/> (plus the images in <paramref name="imagePaths"/> as
        /// base64 <c>image</c> blocks after the text) as a user message. Content is an array of blocks
        /// (CLI 2.1.162 rejected a plain-string content). Non-ASCII is escaped as <c>\uXXXX</c>, so the
        /// line survives whatever encoding the redirected stdin uses under .NET Framework. An image that
        /// cannot be read is replaced by a short text note rather than failing the turn.
        /// </summary>
        public static string BuildUserMessage(string prompt, IEnumerable<string>? imagePaths = null)
        {
            var content = new JArray
            {
                new JObject { ["type"] = "text", ["text"] = prompt ?? "" },
            };
            foreach (var path in imagePaths ?? Array.Empty<string>())
            {
                var mediaType = CliImageAttachments.MediaTypeFor(path) ?? "image/png";
                string data;
                try
                {
                    data = Convert.ToBase64String(File.ReadAllBytes(path));
                }
                catch (Exception ex)
                {
                    content.Add(new JObject
                    {
                        ["type"] = "text",
                        ["text"] = $"(The attached image {path} could not be read: {ex.Message})",
                    });
                    continue;
                }
                content.Add(new JObject
                {
                    ["type"] = "image",
                    ["source"] = new JObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = mediaType,
                        ["data"] = data,
                    },
                });
            }

            var message = new JObject
            {
                ["type"] = "user",
                ["message"] = new JObject
                {
                    ["role"] = "user",
                    ["content"] = content,
                },
            };
            return JsonConvert.SerializeObject(message, new JsonSerializerSettings
            {
                StringEscapeHandling = StringEscapeHandling.EscapeNonAscii,
                Formatting = Formatting.None,
            }) + "\n";
        }
    }
}
