using System;
using System.Collections.Generic;
using System.IO;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Decides which attached images travel as base64 <c>image</c> blocks inside the stream-json user
    /// message (<see cref="StreamJsonInput"/>) instead of as <c>@path</c> references. An <c>@path</c>
    /// image is only expanded up to 256 KiB (see <see cref="CliAttachmentHint"/>); an image block has no
    /// such limit — the CLI downscales it itself before the API call.
    /// <para>
    /// Measured against CLI 2.1.287 (2026-10-08, Read tool disabled, a word printed on each image):
    /// PNG 0.19 → 14.3 MB at 1920×1080…3400×2000, a 34.6 MB 5000×3200 PNG, a 9000×1400 PNG, JPEG, GIF and
    /// BMP (converted by the CLI), and 5 images totalling 38 MB in one prompt — all read correctly, in
    /// order. The session JSONL stores the downscaled copy (~0.6–0.8 MB per image); <c>--resume</c> and
    /// <c>/compact</c> keep it. The caps below stay inside that tested range; anything beyond keeps the
    /// old <c>@path</c> + Read-hint route.
    /// </para>
    /// </summary>
    public static class CliImageAttachments
    {
        /// <summary>Largest single image sent inline (tested: 34.6 MB).</summary>
        public const long MaxImageBytes = 32L * 1024 * 1024;

        /// <summary>Largest total of inline images per prompt (tested: 38 MB in 5 images).</summary>
        public const long MaxTotalBytes = 36L * 1024 * 1024;

        private static readonly Dictionary<string, string> MediaTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["png"] = "image/png",
            ["jpg"] = "image/jpeg",
            ["jpeg"] = "image/jpeg",
            ["gif"] = "image/gif",
            ["webp"] = "image/webp",
            ["bmp"] = "image/bmp",
        };

        /// <summary>The media type for an inline-capable image, or null (by extension).</summary>
        public static string? MediaTypeFor(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var ext = Path.GetExtension(path!.Trim());
            return ext.Length > 1 && MediaTypes.TryGetValue(ext.Substring(1), out var type) ? type : null;
        }

        /// <summary>
        /// The subset of <paramref name="paths"/> to send as image blocks, in their original order:
        /// supported format, existing file, within <see cref="MaxImageBytes"/>, and while the running
        /// total stays within <see cref="MaxTotalBytes"/>. Duplicates (case-insensitive) count once.
        /// </summary>
        public static IReadOnlyList<string> SelectInline(IEnumerable<string>? paths)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var p in paths ?? Array.Empty<string>())
            {
                if (MediaTypeFor(p) == null || !seen.Add(p))
                    continue;
                long size;
                try
                {
                    var info = new FileInfo(p);
                    if (!info.Exists)
                        continue;
                    size = info.Length;
                }
                catch
                {
                    continue;
                }
                if (size == 0 || size > MaxImageBytes || total + size > MaxTotalBytes)
                    continue;
                total += size;
                result.Add(p);
            }
            return result;
        }
    }
}
