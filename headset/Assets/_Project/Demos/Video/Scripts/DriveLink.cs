using System.Text.RegularExpressions;

namespace CardboardXR.Demos.Video
{
    // Google Drive share links. A share link opens Drive's page for the file, not the file, so it's
    // turned into a direct download. confirm=t skips the page Drive shows instead of large files,
    // saying it can't scan them for viruses. The file must be shared as "anyone with the link".
    public static class DriveLink
    {
        static readonly Regex k_PathId = new(@"/(?:file/)?d/([A-Za-z0-9_-]{10,})");
        static readonly Regex k_QueryId = new(@"[?&]id=([A-Za-z0-9_-]{10,})");

        // Handles drive.google.com/file/d/<id>/view, open?id=<id>, uc?id=<id> and
        // drive.usercontent.google.com/download?id=<id>.
        public static bool TryGetFileId(string url, out string id)
        {
            id = null;
            if (string.IsNullOrWhiteSpace(url) || !url.Contains("google.com"))
                return false;
            var match = k_PathId.Match(url);
            if (!match.Success)
                match = k_QueryId.Match(url);
            if (!match.Success)
                return false;
            id = match.Groups[1].Value;
            return true;
        }

        public static string DownloadUrl(string id) =>
            $"https://drive.usercontent.google.com/download?id={id}&export=download&confirm=t";

        // The file name from a Content-Disposition header, such as
        // attachment; filename="beach_360.mp4"; filename*=UTF-8''beach_360.mp4, or null.
        public static string FileNameFromDisposition(string header)
        {
            if (string.IsNullOrEmpty(header))
                return null;
            var encoded = Regex.Match(header, @"filename\*=(?:UTF-8'')?([^;]+)", RegexOptions.IgnoreCase);
            if (encoded.Success)
                return System.Uri.UnescapeDataString(encoded.Groups[1].Value.Trim().Trim('"'));
            var plain = Regex.Match(header, @"filename=""?([^"";]+)""?", RegexOptions.IgnoreCase);
            return plain.Success ? plain.Groups[1].Value.Trim() : null;
        }
    }
}
