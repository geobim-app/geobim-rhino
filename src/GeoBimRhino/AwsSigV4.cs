// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GeoBimRhino
{
    /// <summary>
    /// AWS Signature Version 4 for a single request (the Authorization header),
    /// as S3 expects it. No Rhino types and no other dependencies, so it can be
    /// checked against AWS's published examples (tests/sigv4_check).
    /// </summary>
    public static class AwsSigV4
    {
        /// <param name="headers">headers to sign, lower-case names (must include host and x-amz-date)</param>
        /// <param name="payloadHash">hex SHA-256 of the body, or "UNSIGNED-PAYLOAD"</param>
        public static string Authorization(string method, string canonicalUri, string canonicalQuery,
                                           IDictionary<string, string> headers, string payloadHash,
                                           string accessKey, string secretKey, DateTime utc, string region, string service)
        {
            string amzDate = AmzDate(utc);
            string date = utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var sorted = headers.OrderBy(h => h.Key, StringComparer.Ordinal).ToList();
            string signedHeaders = string.Join(";", sorted.Select(h => h.Key));
            string canonicalHeaders = string.Concat(sorted.Select(h => h.Key + ":" + h.Value.Trim() + "\n"));
            string canonicalRequest = method + "\n" + canonicalUri + "\n" + (canonicalQuery ?? "") + "\n" +
                                      canonicalHeaders + "\n" + signedHeaders + "\n" + payloadHash;

            string scope = $"{date}/{region}/{service}/aws4_request";
            string stringToSign = "AWS4-HMAC-SHA256\n" + amzDate + "\n" + scope + "\n" + Hex(Sha256(Encoding.UTF8.GetBytes(canonicalRequest)));
            byte[] k = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
            k = Hmac(k, region);
            k = Hmac(k, service);
            k = Hmac(k, "aws4_request");
            string signature = Hex(Hmac(k, stringToSign));
            return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
        }

        public static string AmzDate(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        /// <summary>RFC 3986 encoding as S3 expects it for one path segment (unreserved characters stay)</summary>
        public static string UriEncode(string s)
        {
            var sb = new StringBuilder();
            foreach (byte b in Encoding.UTF8.GetBytes(s))
            {
                char c = (char)b;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
                    sb.Append(c);
                else
                    sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>A path (bucket/key) encoded segment by segment, slashes kept</summary>
        public static string EncodePath(string path) => string.Join("/", path.Split('/').Select(UriEncode));

        static byte[] Sha256(byte[] data) { using (var h = SHA256.Create()) return h.ComputeHash(data); }
        static byte[] Hmac(byte[] key, string data) { using (var h = new HMACSHA256(key)) return h.ComputeHash(Encoding.UTF8.GetBytes(data)); }
        static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));
    }
}
