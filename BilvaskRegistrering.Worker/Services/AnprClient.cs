using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BilvaskRegistrering.Worker.Services;

internal sealed class AnprClient
{
    private readonly HttpClient _http;
    private readonly string _token;

    public AnprClient(string token)
    {
        _token = token ?? string.Empty;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<string?> RecognizePlateAsync(byte[] jpgBytes, CancellationToken ct)
    {
        if (jpgBytes == null || jpgBytes.Length == 0) return null;
        if (string.IsNullOrWhiteSpace(_token)) return null;

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(jpgBytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
        form.Add(file, "upload", "frame.jpg");

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.platerecognizer.com/v1/plate-reader/");
        req.Headers.Authorization = new AuthenticationHeaderValue("Token", _token);
        req.Content = form;

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return null;
        if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) return null;

        var plate = results[0].GetProperty("plate").GetString();
        if (string.IsNullOrWhiteSpace(plate)) return null;
        return plate.ToUpperInvariant().Replace(" ", "");
    }
}
