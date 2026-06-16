namespace CodeAstrogator.Core
{
    /// <summary>
    /// Formats file references for the CLI prompt (the <c>@&lt;path&gt;</c> tokens the CLI
    /// expands into file contents). Kept UI-free so the quoting rules are unit-tested.
    /// </summary>
    public static class CliReferenceFormatter
    {
        /// <summary>
        /// Builds the CLI <c>@</c>-reference for a file. The CLI's @-parser stops at the
        /// first whitespace, so a path containing a space (e.g. a Windows profile like
        /// "C:\Users\Jan Huels\…", where pasted screenshots land under LocalAppData) would
        /// otherwise be truncated and the file silently dropped. We wrap such paths in
        /// quotes — <c>@"C:\a b\f.png"</c> — which the CLI accepts; the optional <c>#L</c>
        /// line suffix stays outside the quotes (<c>@"…\f.cs"#L10-20</c>). Space-free paths
        /// are emitted unquoted, exactly as before.
        /// </summary>
        public static string FormatFileReference(string path, string? lineSuffix = null)
        {
            var needsQuote = !string.IsNullOrEmpty(path) && path.IndexOfAny(new[] { ' ', '\t' }) >= 0;
            var token = needsQuote ? "\"" + path + "\"" : path;
            return "@" + token + (lineSuffix ?? "");
        }
    }
}
