using System;
using UnityEngine;
using UnityEngine.Networking;

namespace CardboardXR.Stereo
{
    // Follows a scanned URL's redirects until it reaches a Cardboard viewer URL, like the Cardboard
    // SDK's followCardboardParamRedirect: at most five redirects, always over https.
    public static class ViewerUrlResolver
    {
        const int k_MaxRedirects = 5;
        const int k_TimeoutSeconds = 5;

        public enum Status
        {
            Ok,
            NotAViewer,
            ConnectionError,
        }

        public static async Awaitable<(Status status, string url)> ResolveAsync(string scanned)
        {
            var url = ViewerParamsCodec.NormalizeUrl(scanned);
            for (var redirects = 0; !ViewerParamsCodec.IsViewerUrl(url); redirects++)
            {
                if (redirects >= k_MaxRedirects ||
                    !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps)
                    return (Status.NotAViewer, url);

                using var request = UnityWebRequest.Get(uri);
                // Read each redirect ourselves instead of letting the request follow it.
                request.redirectLimit = 0;
                request.timeout = k_TimeoutSeconds;
                await request.SendWebRequest();

                var location = request.GetResponseHeader("Location");
                if (string.IsNullOrEmpty(location))
                {
                    var status = request.result == UnityWebRequest.Result.ConnectionError
                        ? Status.ConnectionError
                        : Status.NotAViewer;
                    Debug.Log($"[Viewer] {url} did not redirect ({request.responseCode} {request.error})");
                    return (status, url);
                }

                url = ViewerParamsCodec.NormalizeUrl(new Uri(uri, location).ToString());
            }

            return (Status.Ok, url);
        }
    }
}
