using System.Text;

namespace DiskLoom.Services;

internal static class CreatorIdentity
{
    private const string EncodedAboutLine = "Q3JlYXRlZCBhbmQgb3duZWQgYnkgTmF4dGVycmEgwrcgZ2l0aHViQHNoYWRlcy5kZXY=";

    internal static string AboutLine =>
        Encoding.UTF8.GetString(Convert.FromBase64String(EncodedAboutLine));
}
