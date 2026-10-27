using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace WeatherImages.Services;

/// <summary>Very small API key check for the "request" endpoint. Header: <c>x-api-key</c>.</summary>
public static class ApiKeyAuthorizer
{
    public const string HeaderName = "x-api-key";

    /// <summary>True when no key is configured (auth disabled) or the request carries the right key.</summary>
    public static bool IsAuthorized(HttpRequest request, string? expectedKey)
    {
        if (string.IsNullOrEmpty(expectedKey))
        {
            return true;
        }

        var provided = request.Headers[HeaderName].ToString();
        return FixedTimeEquals(provided, expectedKey);
    }

    internal static bool FixedTimeEquals(string provided, string expected)
    {
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b); // length mismatch simply returns false
    }
}
