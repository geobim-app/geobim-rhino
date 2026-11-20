// Checks AwsSigV4 against the examples in AWS's S3 documentation
// ("Signature calculations for the Authorization header: transferring payload in
// a single chunk", examples GET Object and PUT Object; example credentials).
using System;
using System.Collections.Generic;
using GeoBimRhino;

static class Program
{
    const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    static readonly DateTime When = new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc);
    static int _failed;

    static void Check(string name, string authorization, string expectedSignature)
    {
        bool ok = authorization.EndsWith("Signature=" + expectedSignature, StringComparison.Ordinal);
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name + (ok ? "" : "\n     got " + authorization));
        if (!ok) _failed++;
    }

    static int Main()
    {
        // GET Object: GET /test.txt, Range: bytes=0-9, empty body
        Check("GET Object", AwsSigV4.Authorization("GET", "/test.txt", "",
            new Dictionary<string, string>
            {
                ["host"] = "examplebucket.s3.amazonaws.com",
                ["range"] = "bytes=0-9",
                ["x-amz-content-sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                ["x-amz-date"] = AwsSigV4.AmzDate(When)
            },
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            AccessKey, SecretKey, When, "us-east-1", "s3"),
            "f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41");

        // PUT Object: PUT /test$file.text, body "Welcome to Amazon S3."
        string body = "44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072";   // SHA-256 of the body
        Check("PUT Object", AwsSigV4.Authorization("PUT", "/" + AwsSigV4.EncodePath("test$file.text"), "",
            new Dictionary<string, string>
            {
                ["date"] = "Fri, 24 May 2013 00:00:00 GMT",
                ["host"] = "examplebucket.s3.amazonaws.com",
                ["x-amz-content-sha256"] = body,
                ["x-amz-date"] = AwsSigV4.AmzDate(When),
                ["x-amz-storage-class"] = "REDUCED_REDUNDANCY"
            },
            body, AccessKey, SecretKey, When, "us-east-1", "s3"),
            "98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd");

        Console.WriteLine(_failed == 0 ? "all SigV4 checks passed" : _failed + " SigV4 check(s) failed");
        return _failed == 0 ? 0 : 1;
    }
}
