using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BilvaskRegistrering.Security;

internal sealed record InstallLicensePayload(string? p, string? v, string? exp, string? n);

internal static class InstallCodeVerifier
{
    // RSA public key (XML) embedded in the application.
    // Keep the PRIVATE key offline and never ship it.
    private const string PublicKeyXml = @"<RSAKeyValue><Modulus>skh0dEhY94qAzVw4pUbLRgbirx5Bdv0ROtLYHy6apEWA3CA3XUX/9Zp7reXYSFApSbbbZozz6NP9GsV1pRUSMqVihdIcd+HO1tanoDofStYkhBLVPjdXW6D8e+4q0tMAobLKi+PFRbpUi9LfiYViCPX5lHuRdmRQfQZjiY8iAXj9FIT81fdfi8Ut2UJ/gf2xlsBKM+im+wcEqW/5fnJcaEZWBxilBdrqmeZo2hQFk5JOIKA+/iaKY37SvewjK7BAmaZ37DdJ5EueaB0KOUMHxu8Otfv48Cf4G/U13EZatWOSJ5v+P3gFNJZxW45KdF+8RZz2ry/jihWk6G7utYdjCQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    public static bool TryVerify(string? code, out InstallLicensePayload payload, out string error)
    {
        payload = new InstallLicensePayload(null, null, null, null);
        error = string.Empty;

        code = (code ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            error = "Mangler installasjonskode.";
            return false;
        }

        var parts = code.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !string.Equals(parts[0], "BVR1", StringComparison.Ordinal))
        {
            error = "Ugyldig format på installasjonskoden.";
            return false;
        }

        byte[] payloadBytes;
        byte[] sigBytes;
        try
        {
            payloadBytes = Base64UrlDecode(parts[1]);
            sigBytes = Base64UrlDecode(parts[2]);
        }
        catch
        {
            error = "Ugyldig format på installasjonskoden.";
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(PublicKeyXml);

            var ok = rsa.VerifyData(payloadBytes, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!ok)
            {
                error = "Ugyldig installasjonskode.";
                return false;
            }

            var json = Encoding.UTF8.GetString(payloadBytes);
            payload = JsonSerializer.Deserialize<InstallLicensePayload>(json) ?? payload;

            // basic sanity checks
            if (!string.Equals(payload.p, "BilvaskRegistrering", StringComparison.OrdinalIgnoreCase))
            {
                error = "Installasjonskoden gjelder ikke for dette produktet.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(payload.exp))
            {
                if (DateTime.TryParse(payload.exp, out var exp))
                {
                    // treat exp as end-of-day local; compare using date only to avoid timezone surprises
                    if (DateTime.Now.Date > exp.Date)
                    {
                        error = "Installasjonskoden er utlĂ¸pt.";
                        return false;
                    }
                }
            }

            return true;
        }
        catch
        {
            error = "Ugyldig installasjonskode.";
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}

