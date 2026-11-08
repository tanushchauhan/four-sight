using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace CardboardXR.Demos.Video
{
    // Downloads a Google Drive video into the cache, then points its entry at the file. Playing it
    // straight from Drive was the other choice, but Drive throttles and interrupts long reads, so a
    // stream would stall mid-video. Nothing else in the app needs a connection: without one the
    // entry just says so. The cache is capped, oldest first out, since the phone's storage is shared
    // with everything else and 360 videos are large.
    public static class DriveDownloader
    {
        // Downloads beyond this, in bytes, remove the least recently used until the cache fits.
        const long k_CacheBytes = 4L * 1024 * 1024 * 1024;

        public static IEnumerator Download(VideoEntry entry, Action<float> progress)
        {
            var folder = Path.Combine(VideoLibrary.CacheFolder, entry.DriveId);
            var partial = Path.Combine(VideoLibrary.CacheFolder, entry.DriveId + ".part");
            Directory.CreateDirectory(VideoLibrary.CacheFolder);

            using var request = UnityWebRequest.Get(DriveLink.DownloadUrl(entry.DriveId));
            request.downloadHandler = new DownloadHandlerFile(partial) { removeFileOnAbort = true };
            var started = Time.realtimeSinceStartup;
            Debug.Log($"[Video] Downloading {entry.Title} from Google Drive");
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                progress?.Invoke(request.downloadProgress);
                entry.Status = $"Downloading {request.downloadProgress * 100f:F0}% ({request.downloadedBytes / 1e6:F0} MB)";
                yield return null;
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                entry.Status = $"Download failed: {request.error}";
                Debug.LogWarning($"[Video] Downloading {entry.Title} failed: {request.error} (HTTP {request.responseCode})");
                TryDelete(partial);
                yield break;
            }

            // Drive answers a file it won't hand over (not shared, or over its download quota) with a
            // web page instead.
            var type = request.GetResponseHeader("Content-Type") ?? "";
            if (type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                entry.Status = "Drive sent a web page, not the video: share it as \"Anyone with the link\"";
                Debug.LogWarning($"[Video] Drive returned a page for {entry.Title}, not the file: it's not shared publicly, or over quota");
                TryDelete(partial);
                yield break;
            }

            // Only a plain file name from the header: no folders, and nothing that names the folder itself.
            var name = Path.GetFileName(DriveLink.FileNameFromDisposition(request.GetResponseHeader("Content-Disposition")) ?? "");
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
                name = entry.DriveId + ".mp4";
            var file = Path.Combine(folder, name);
            try
            {
                Directory.CreateDirectory(folder);
                TryDelete(file);
                File.Move(partial, file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                entry.Status = $"Couldn't save the download: {e.Message}";
                Debug.LogWarning($"[Video] Saving {entry.Title} as {file} failed: {e.Message}");
                TryDelete(partial);
                yield break;
            }

            var megabytes = new FileInfo(file).Length / 1e6;
            Debug.Log($"[Video] Downloaded {name}: {megabytes:F0} MB in {Time.realtimeSinceStartup - started:F0} s");
            // A title from the list was the user's; otherwise the file's own name, whose tags may say
            // the format.
            if (entry.Title.StartsWith("Drive video ", StringComparison.Ordinal))
            {
                entry.Title = Path.GetFileNameWithoutExtension(name);
                entry.Format = VideoFormat.FromFileName(name);
            }
            entry.Url = file;
            entry.Status = null;
            TrimCache(folder);
        }

        // Removes the least recently used downloads until the cache fits, keeping the one just made.
        static void TrimCache(string keep)
        {
            try
            {
                var folders = new DirectoryInfo(VideoLibrary.CacheFolder).GetDirectories();
                Array.Sort(folders, (a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));
                var total = 0L;
                foreach (var folder in folders)
                    total += Size(folder);
                foreach (var folder in folders)
                {
                    if (total <= k_CacheBytes)
                        break;
                    if (string.Equals(folder.FullName.TrimEnd('/'), keep.TrimEnd('/'), StringComparison.Ordinal))
                        continue;
                    total -= Size(folder);
                    folder.Delete(true);
                    Debug.Log($"[Video] Removed {folder.Name} from the download cache to stay under {k_CacheBytes / 1e9:F0} GB");
                }
            }
            catch (IOException e)
            {
                Debug.LogWarning($"[Video] Couldn't trim the download cache: {e.Message}");
            }
        }

        static long Size(DirectoryInfo folder)
        {
            var size = 0L;
            foreach (var file in folder.GetFiles())
                size += file.Length;
            return size;
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
