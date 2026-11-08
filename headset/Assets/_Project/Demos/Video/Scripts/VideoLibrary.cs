using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CardboardXR.Demos.Video
{
    // One video the player can show.
    public class VideoEntry
    {
        public string Title;
        public VideoFormat Format;
        // Something VideoPlayer.url takes: a file path, or a StreamingAssets path (inside the APK on
        // Android). Null until a Drive video is downloaded.
        public string Url;
        // A Google Drive file, downloaded into the cache before it plays.
        public string DriveId;
        // Where a download stands, while one runs or after one failed.
        public string Status;

        public bool NeedsDownload => Url == null && DriveId != null;
    }

    // Finds the videos to offer: the ones bundled with the app, files the user copied into the
    // Videos folder, and Google Drive links listed in videos.txt in that folder.
    //   Android: /storage/emulated/0/Android/data/com.brenpoly.cardboardxr/files/Videos
    //            (adb push beach_360.mp4 there; no storage permission needed)
    //   iPhone:  the app's Documents/Videos, which Finder (the phone's Files tab) and the Files app
    //            can copy into, since the app shares its Documents folder
    // videos.txt takes one link per line, optionally followed by a name, whose tags say the format
    // (see VideoFormat.FromFileName). Lines starting with # are skipped. A downloaded video is kept
    // in the cache, named by its Drive id, so it only downloads once.
    public static class VideoLibrary
    {
        public const string ListFileName = "videos.txt";
        static readonly string[] k_Extensions = { ".mp4", ".mov", ".m4v", ".webm" };

        public static string VideoFolder => Path.Combine(Application.persistentDataPath, "Videos");
        public static string CacheFolder => Path.Combine(Application.temporaryCachePath, "DriveVideos");

        // placeholders are the repository's own bundled videos; count is how many bundled videos were found.
        public static List<VideoEntry> Find(IEnumerable<string> placeholders, out int count)
        {
            var entries = new List<VideoEntry>();
            var bundled = OrderBundled(StreamingAssetsFiles(), placeholders);
            count = bundled.Count;
            foreach (var name in bundled)
            {
                entries.Add(new VideoEntry
                {
                    Title = Path.GetFileNameWithoutExtension(name),
                    Format = VideoFormat.FromFileName(name),
                    Url = Path.Combine(Application.streamingAssetsPath, name),
                });
            }

            try
            {
                // Made even when empty, so there's somewhere visible to copy videos into.
                Directory.CreateDirectory(VideoFolder);
                var files = new List<string>(Directory.GetFiles(VideoFolder));
                files.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    if (!IsVideo(file))
                        continue;
                    entries.Add(new VideoEntry
                    {
                        Title = Path.GetFileNameWithoutExtension(file),
                        Format = VideoFormat.FromFileName(file),
                        Url = file,
                    });
                }

                var list = Path.Combine(VideoFolder, ListFileName);
                if (File.Exists(list))
                    entries.AddRange(ParseList(File.ReadAllLines(list)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Video] Couldn't read {VideoFolder}: {e.Message}");
            }
            return entries;
        }

        static bool IsVideo(string path) => Array.IndexOf(k_Extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

        // The video files among present: your own first, by name, then the placeholders that are there,
        // in their order. Pure, for testing.
        public static List<string> OrderBundled(IEnumerable<string> present, IEnumerable<string> placeholders)
        {
            var own = new List<string>();
            foreach (var name in present)
            {
                if (IsVideo(name))
                    own.Add(name);
            }
            var found = new List<string>();
            foreach (var name in placeholders)
            {
                var index = own.FindIndex(file => string.Equals(file, name, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    continue;
                found.Add(own[index]);
                own.RemoveAt(index);
            }
            own.Sort(StringComparer.OrdinalIgnoreCase);
            own.AddRange(found);
            return own;
        }

        // The file names at the top of StreamingAssets. On Android they're inside the APK, where only
        // its AssetManager can list them.
        static IEnumerable<string> StreamingAssetsFiles()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var assets = activity.Call<AndroidJavaObject>("getAssets");
                return assets.Call<string[]>("list", "") ?? Array.Empty<string>();
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"[Video] Couldn't list the bundled videos: {e.Message}");
                return Array.Empty<string>();
            }
#else
            var folder = Application.streamingAssetsPath;
            if (!Directory.Exists(folder))
                return Array.Empty<string>();
            var names = new List<string>();
            foreach (var file in Directory.GetFiles(folder))
                names.Add(Path.GetFileName(file));
            return names;
#endif
        }

        // The Drive videos listed in videos.txt, with the ones already downloaded ready to play.
        public static List<VideoEntry> ParseList(IEnumerable<string> lines)
        {
            var entries = new List<VideoEntry>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                var space = line.IndexOfAny(new[] { ' ', '\t' });
                var link = space < 0 ? line : line.Substring(0, space);
                var name = space < 0 ? null : line.Substring(space + 1).Trim();
                if (!DriveLink.TryGetFileId(link, out var id))
                {
                    Debug.LogWarning($"[Video] Not a Google Drive link, skipped: {link}");
                    continue;
                }

                var cached = CachedFile(id);
                var fileName = name ?? (cached != null ? Path.GetFileName(cached) : null);
                entries.Add(new VideoEntry
                {
                    Title = fileName != null ? Path.GetFileNameWithoutExtension(fileName) : $"Drive video {id.Substring(0, 6)}",
                    Format = VideoFormat.FromFileName(fileName),
                    Url = cached,
                    DriveId = id,
                });
            }
            return entries;
        }

        // The downloaded file for a Drive id, if it's in the cache.
        public static string CachedFile(string id)
        {
            var folder = Path.Combine(CacheFolder, id);
            if (!Directory.Exists(folder))
                return null;
            var files = Directory.GetFiles(folder);
            return files.Length > 0 ? files[0] : null;
        }
    }
}
