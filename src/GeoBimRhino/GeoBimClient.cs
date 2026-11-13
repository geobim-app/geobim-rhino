// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GeoBimRhino
{
    /// <summary>
    /// geoBIM.app API as used by the plugin (all with the plugin token):
    ///   api/plugin-whoami.php       — test connection
    ///   api/pointcloud-upload.php   — upload (IFC), optional replace=&lt;slug&gt;
    ///   api/pointcloud-status.php   — conversion status of a job
    /// </summary>
    public class GeoBimClient
    {
        static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        readonly string _server;
        readonly string _token;

        public GeoBimClient(string server, string token)
        {
            _server = (server ?? GeoBimPlugin.DefaultServer).TrimEnd('/');
            _token = token ?? "";
        }

        public class ApiException : Exception
        {
            public HttpStatusCode Status { get; }
            public ApiException(HttpStatusCode status, string message) : base(message) { Status = status; }
        }

        HttpRequestMessage Request(HttpMethod method, string path)
        {
            var req = new HttpRequestMessage(method, _server + "/" + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            req.Headers.UserAgent.ParseAdd("geobim-rhino/" + GeoBimPlugin.PluginVersion);
            return req;
        }

        static async Task<JsonElement> Read(HttpResponseMessage resp)
        {
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonElement json;
            try { json = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone(); }
            catch (JsonException) { throw new ApiException(resp.StatusCode, $"HTTP {(int)resp.StatusCode}: unexpected answer from the server"); }
            if (!resp.IsSuccessStatusCode)
            {
                var msg = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var err) ? err.GetString() : null;
                throw new ApiException(resp.StatusCode, msg ?? $"HTTP {(int)resp.StatusCode}");
            }
            return json;
        }

        public async Task<JsonElement> WhoAmI(CancellationToken ct = default)
        {
            using (var req = Request(HttpMethod.Get, "api/plugin-whoami.php"))
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                using (var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false))
                    return await Read(resp).ConfigureAwait(false);
            }
        }

        public class UploadResult { public string JobId; public string Slug; public bool Replaced; }

        public async Task<UploadResult> Upload(string filePath, string name, string replaceSlug, IProgress<double> progress, CancellationToken ct = default)
        {
            using (var req = Request(HttpMethod.Post, "api/pointcloud-upload.php"))
            using (var form = new MultipartFormDataContent())
            using (var stream = File.OpenRead(filePath))
            {
                var file = new ProgressStreamContent(stream, progress);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(file, "file", Path.GetFileName(filePath));
                form.Add(new StringContent(name ?? ""), "name");
                if (!string.IsNullOrEmpty(replaceSlug)) form.Add(new StringContent(replaceSlug), "replace");
                req.Content = form;
                using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var json = await Read(resp).ConfigureAwait(false);
                    return new UploadResult
                    {
                        JobId = json.GetProperty("jobId").GetString(),
                        Slug = json.GetProperty("slug").GetString(),
                        Replaced = json.TryGetProperty("replace", out var r) && r.ValueKind == JsonValueKind.True
                    };
                }
            }
        }

        public async Task<JsonElement> Status(string jobId, CancellationToken ct = default)
        {
            using (var req = Request(HttpMethod.Get, "api/pointcloud-status.php?job=" + Uri.EscapeDataString(jobId)))
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                using (var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false))
                    return await Read(resp).ConfigureAwait(false);
            }
        }

        public string ModelUrl(string slug) => _server + "/?model=" + Uri.EscapeDataString(slug);

        /// <summary>Upload body that reports how much has been sent (0–1)</summary>
        class ProgressStreamContent : HttpContent
        {
            readonly Stream _stream;
            readonly IProgress<double> _progress;

            public ProgressStreamContent(Stream stream, IProgress<double> progress)
            {
                _stream = stream;
                _progress = progress;
            }

            protected override async Task SerializeToStreamAsync(Stream target, TransportContext context)
            {
                var buffer = new byte[81920];
                long total = _stream.Length, sent = 0;
                int read;
                while ((read = await _stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    sent += read;
                    _progress?.Report(total > 0 ? (double)sent / total : 1);
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _stream.Length;
                return true;
            }
        }
    }
}
