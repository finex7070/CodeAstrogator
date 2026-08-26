using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeAstrogator.Core
{
    /// <summary>
    /// Attached images are handed to the CLI as <c>@&lt;path&gt;</c> references, which the CLI expands
    /// into real image blocks — but <b>only up to 256 KiB per file</b>. A bigger image is dropped
    /// <b>silently</b>: the model receives the path as text and nothing else, and typically answers
    /// "I only see the file names, no content" (verified against CLI 2.1.224: 262074 bytes arrives,
    /// 281505 bytes does not; the limit is per file, not per prompt, and independent of the pixel
    /// size). Pasted screenshots are routinely 0.4–2.4 MB, so this hit almost every screenshot.
    /// The model <em>can</em> see such a file when it opens it with the Read tool, so the prompt gets
    /// an explicit instruction to do exactly that for the affected files.
    /// </summary>
    public static class CliAttachmentHint
    {
        /// <summary>Largest image the CLI still expands into an image block (256 KiB).</summary>
        public const long InlineImageLimitBytes = 262144;

        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "png", "jpg", "jpeg", "gif", "bmp", "webp", "ico", "apng", "avif", "tif", "tiff",
        };

        /// <summary>True when the path looks like a raster image (by extension).</summary>
        public static bool IsImagePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            var ext = Path.GetExtension(path!.Trim());
            return ext.Length > 1 && ImageExtensions.Contains(ext.Substring(1));
        }

        /// <summary>
        /// True when <paramref name="path"/> is an image whose content the CLI will drop. Sizes are
        /// read from disk; an unreadable/missing file counts as fine (nothing to warn about).
        /// </summary>
        public static bool IsOversizedImage(string? path)
        {
            if (!IsImagePath(path))
                return false;
            try
            {
                var info = new FileInfo(path!);
                return info.Exists && info.Length > InlineImageLimitBytes;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The hint appended to the prompt, or null when no attached image is oversized. Lists the
        /// full paths so the model can pass them straight to Read.
        /// </summary>
        public static string? BuildReadHint(IEnumerable<string>? oversizedPaths)
        {
            var paths = (oversizedPaths ?? Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0)
                return null;

            var sb = new StringBuilder();
            sb.Append("Note: the following image file")
              .Append(paths.Count == 1 ? " is" : "s are")
              .Append(" larger than the CLI's 256 KiB inline limit, so ")
              .Append(paths.Count == 1 ? "its" : "their")
              .Append(" content is NOT part of this prompt — only the path")
              .Append(paths.Count == 1 ? "" : "s")
              .Append(". Open ")
              .Append(paths.Count == 1 ? "it" : "them")
              .Append(" with the Read tool before answering:");
            foreach (var p in paths)
                sb.Append('\n').Append(p);
            return sb.ToString();
        }
    }
}
