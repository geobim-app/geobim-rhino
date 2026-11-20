// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GeoBimRhino
{
    /// <summary>
    /// Cesium ion REST API (https://cesium.com/learn/ion/ion-upload-rest/), with an
    /// access token that has the scopes assets:list, assets:read and assets:write:
    ///   1. POST /v1/assets: name, type 3DTILES, options {sourceType BIM_CAD,
    ///      position [lon, lat, height]} → assetMetadata, uploadLocation (temporary
    ///      S3 credentials, about an hour), onComplete
    ///   2. PUT the file to S3 under uploadLocation.prefix (AWS Signature V4,
    ///      path-style URL because the bucket name contains dots)
    ///   3. POST onComplete.url → ion starts tiling
    ///   4. GET /v1/assets/{id} until status COMPLETE / DATA_ERROR / ERROR
    /// BIM_CAD (not 3D_MODEL) follows lab.geobim.app's uploadToIon: 3D_MODEL with a
    /// position has been reported to stall. No AWS SDK: the plugin stays one .rhp.
    /// </summary>
    public class IonClient
    {
        public const string Api = "https://api.cesium.com/v1";
        static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        readonly string _token;

        public IonClient(string token) { _token = token ?? ""; }

        public class ApiException : Exception
        {
            public HttpStatusCode Status { get; }
            public ApiException(HttpStatusCode status, string message) : base(message) { Status = status; }
        }

        public class UploadLocation
        {
            public string Bucket, Prefix, Endpoint, AccessKey, SecretAccessKey, SessionToken;
            /// <summary>The account's storage region, e.g. eu-west-1 (ion sends it; us-east-1 when missing)</summary>
            public string Region = "us-east-1";
        }

        public class NewAsset
        {
            public long Id;
            public UploadLocation Upload;
            public string CompleteMethod = "POST", CompleteUrl;
            public string CompleteFields = "{}";
        }

        HttpRequestMessage Request(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            req.Headers.UserAgent.ParseAdd("geobim-rhino/" + GeoBimPlugin.PluginVersion);
            return req;
        }

        static async Task<JsonElement> Read(HttpResponseMessage resp)
        {
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonElement json;
            try { json = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone(); }
            catch (JsonException) { json = default; }
            if (!resp.IsSuccessStatusCode)
            {
                string msg = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() : null;
                string code = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() : null;
                var method = resp.RequestMessage?.Method;
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    msg = "the Cesium ion token is invalid or expired" + (msg != null ? $" ({msg})" : "");
                else if (resp.StatusCode == HttpStatusCode.Forbidden)
                    msg = "the Cesium ion token lacks a scope — it needs assets:list, assets:read and assets:write" + (msg != null ? $" ({msg})" : "");
                else if (resp.StatusCode == HttpStatusCode.NotFound && code == "ResourceNotFound" && method != null && method != HttpMethod.Get)
                    // ion answers a write without the assets:write scope with 404 ResourceNotFound
                    // ("/v1/assets?assetRegion=us-east-1"), not with 403
                    msg = "Cesium ion refused to create or change an asset — the token most likely lacks the assets:write scope. " +
                          "Create a token with assets:list, assets:read and assets:write at ion.cesium.com → Access Tokens" + (msg != null ? $" (ion: {msg})" : "");
                throw new ApiException(resp.StatusCode, msg ?? $"HTTP {(int)resp.StatusCode}");
            }
            if (json.ValueKind == JsonValueKind.Undefined) throw new ApiException(resp.StatusCode, "unexpected answer from Cesium ion");
            return json;
        }

        /// <summary>Checks the token (needs assets:list)</summary>
        public async Task<int> CountAssets(CancellationToken ct = default)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var req = Request(HttpMethod.Get, Api + "/assets?limit=1"))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                using (var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false))
                {
                    var json = await Read(resp).ConfigureAwait(false);
                    return json.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
                }
            }
        }

        /// <summary>
        /// Whether the token may create assets, without creating one: an empty POST /v1/assets is
        /// refused by ion's validation (4xx other than 404) when the token has assets:write, and
        /// answered with 404 ResourceNotFound when it has not. Null when the answer is unexpected.
        /// </summary>
        public async Task<bool?> CanWrite(CancellationToken ct = default)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var req = Request(HttpMethod.Post, Api + "/assets"))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using (var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false))
                {
                    int s = (int)resp.StatusCode;
                    if (s == 404 || s == 403) return false;
                    if (s >= 400 && s < 500 && s != 401) return true;
                    return null;
                }
            }
        }

        /// <summary>Creates a 3D Tiles asset from a BIM/CAD file whose origin (0,0,0) sits at lon/lat/height (ellipsoidal)</summary>
        public async Task<NewAsset> CreateAsset(string name, string description, double lon, double lat, double height, CancellationToken ct = default)
        {
            var body = new
            {
                name,
                description = description ?? "",
                type = "3DTILES",
                options = new { sourceType = "BIM_CAD", position = new[] { lon, lat, height } }
            };
            using (var req = Request(HttpMethod.Post, Api + "/assets"))
            {
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var json = await Read(resp).ConfigureAwait(false);
                    var a = new NewAsset();
                    a.Id = json.GetProperty("assetMetadata").GetProperty("id").GetInt64();
                    var ul = json.GetProperty("uploadLocation");
                    a.Upload = new UploadLocation
                    {
                        Bucket = Str(ul, "bucket"),
                        Prefix = Str(ul, "prefix") ?? "",
                        Endpoint = Str(ul, "endpoint") ?? "https://s3.amazonaws.com",
                        AccessKey = Str(ul, "accessKey"),
                        SecretAccessKey = Str(ul, "secretAccessKey"),
                        SessionToken = Str(ul, "sessionToken"),
                        Region = Str(ul, "region") ?? RegionFromEndpoint(Str(ul, "endpoint")) ?? "us-east-1"
                    };
                    if (json.TryGetProperty("onComplete", out var oc) && oc.ValueKind == JsonValueKind.Object)
                    {
                        a.CompleteMethod = Str(oc, "method") ?? "POST";
                        a.CompleteUrl = Str(oc, "url");
                        if (oc.TryGetProperty("fields", out var f)) a.CompleteFields = f.GetRawText();
                    }
                    a.CompleteUrl = a.CompleteUrl ?? $"{Api}/assets/{a.Id}/uploadComplete";
                    if (a.Upload.Bucket == null || a.Upload.AccessKey == null || a.Upload.SecretAccessKey == null)
                        throw new ApiException(resp.StatusCode, "Cesium ion sent no upload location");
                    return a;
                }
            }
        }

        /// <summary>"eu-west-1" from https://s3.eu-west-1.amazonaws.com/, else null</summary>
        static string RegionFromEndpoint(string endpoint)
        {
            var m = System.Text.RegularExpressions.Regex.Match(endpoint ?? "", @"s3[.-]([a-z]{2}(?:-[a-z]+)+-\d)\.amazonaws\.com");
            return m.Success ? m.Groups[1].Value : null;
        }

        class S3Error : Exception
        {
            public HttpStatusCode Status; public string Code, Region;
            public S3Error(HttpStatusCode status, string code, string message, string region)
                : base($"S3 upload failed: HTTP {(int)status}{(string.IsNullOrEmpty(code) ? "" : " " + code)}{(string.IsNullOrEmpty(message) ? "" : " — " + message)}")
            { Status = status; Code = code; Region = region; }
        }

        /// <summary>PUT the file to ion's S3 bucket (AWS Signature V4, unsigned payload over HTTPS) in
        /// the account's region; when S3 names another region, once more there</summary>
        public static async Task PutS3(UploadLocation loc, string filePath, IProgress<double> progress, CancellationToken ct = default)
        {
            try { await PutS3(loc, filePath, progress, loc.Region, ct).ConfigureAwait(false); }
            catch (S3Error e) when (e.Code == "AuthorizationHeaderMalformed" && !string.IsNullOrEmpty(e.Region) && e.Region != loc.Region)
            {
                await PutS3(loc, filePath, progress, e.Region, ct).ConfigureAwait(false);
            }
        }

        static async Task PutS3(UploadLocation loc, string filePath, IProgress<double> progress, string region, CancellationToken ct)
        {
            // path-style: https://<endpoint>/<bucket>/<prefix><file>
            var endpoint = new Uri(loc.Endpoint.TrimEnd('/') + "/");
            string key = loc.Prefix + Path.GetFileName(filePath);
            string basePath = endpoint.AbsolutePath.TrimEnd('/');
            string canonicalUri = basePath + "/" + AwsSigV4.UriEncode(loc.Bucket) + "/" + AwsSigV4.EncodePath(key);
            var url = new Uri(endpoint.GetLeftPart(UriPartial.Authority) + canonicalUri);
            string host = url.IsDefaultPort ? url.Host : url.Host + ":" + url.Port;

            var now = DateTime.UtcNow;
            string amzDate = AwsSigV4.AmzDate(now);
            const string payload = "UNSIGNED-PAYLOAD";
            var headers = new System.Collections.Generic.Dictionary<string, string>
            {
                ["host"] = host,
                ["x-amz-content-sha256"] = payload,
                ["x-amz-date"] = amzDate
            };
            if (!string.IsNullOrEmpty(loc.SessionToken)) headers["x-amz-security-token"] = loc.SessionToken;
            string authorization = AwsSigV4.Authorization("PUT", canonicalUri, "", headers, payload,
                                                          loc.AccessKey, loc.SecretAccessKey, now, region, "s3");

            using (var stream = File.OpenRead(filePath))
            using (var req = new HttpRequestMessage(HttpMethod.Put, url))
            {
                req.Headers.TryAddWithoutValidation("Authorization", authorization);
                req.Headers.TryAddWithoutValidation("x-amz-content-sha256", payload);
                req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
                if (!string.IsNullOrEmpty(loc.SessionToken)) req.Headers.TryAddWithoutValidation("x-amz-security-token", loc.SessionToken);
                var content = new GeoBimClient.ProgressStreamContent(stream, progress);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                req.Content = content;
                using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false) ?? "";
                        string Tag(string t) => System.Text.RegularExpressions.Regex.Match(text, "<" + t + ">([^<]+)</" + t + ">").Groups[1].Value;
                        throw new S3Error(resp.StatusCode, Tag("Code"), Tag("Message"), Tag("Region"));
                    }
                }
            }
        }

        /// <summary>Tells ion the files are there (onComplete)</summary>
        public async Task Complete(NewAsset a, CancellationToken ct = default)
        {
            using (var req = Request(new HttpMethod(a.CompleteMethod), a.CompleteUrl))
            {
                req.Content = new StringContent(string.IsNullOrWhiteSpace(a.CompleteFields) ? "{}" : a.CompleteFields, Encoding.UTF8, "application/json");
                using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                    await Read(resp).ConfigureAwait(false);
            }
        }

        /// <summary>Asset metadata: status (AWAITING_FILES, NOT_STARTED, IN_PROGRESS, COMPLETE, DATA_ERROR, ERROR), percentComplete</summary>
        public async Task<JsonElement> GetAsset(long id, CancellationToken ct = default)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var req = Request(HttpMethod.Get, $"{Api}/assets/{id}"))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                using (var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false))
                    return await Read(resp).ConfigureAwait(false);
            }
        }

        public static string AssetUrl(long id) => $"https://ion.cesium.com/assets/{id}";

        static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
