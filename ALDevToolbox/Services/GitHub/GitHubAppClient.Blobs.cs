using System.Net;
using System.Text;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// The Git Data blob read (issue #631).
///
/// <para>The Contents API inlines a file's bytes only up to 1 MB and answers
/// without a <c>content</c> field above that, which
/// <see cref="GitHubAppClient.GetFileAsync"/> reports as "there is nothing to
/// read here". A generated <c>.g.xlf</c> for a real extension passes that mark
/// easily, so the translation-memory ingest needs a route that does not care
/// how big the file is: the blob endpoint, asked for raw bytes by sha - the sha
/// the tree listing already carries.</para>
/// </summary>
public sealed partial class GitHubAppClient
{
    /// <summary>
    /// The media type that makes the blob endpoint return the file's own bytes
    /// rather than a JSON envelope with base64 in it.
    /// </summary>
    private const string RawBlobMediaType = "application/vnd.github.raw+json";

    /// <summary>
    /// One blob's text, by the sha a tree listing gave for it.
    ///
    /// <para>Unlike the Contents API this has no 1 MB inlining limit - GitHub
    /// serves raw blobs up to 100 MB, and refuses larger ones, which is far
    /// above anything an XLIFF reaches. The bytes are decoded as UTF-8 with any
    /// byte-order mark stripped, because an XML parser handed a BOM inside a
    /// string rejects the document.</para>
    ///
    /// <para><see langword="null"/> when GitHub has no such blob - a sha from a
    /// tree that has since been rewritten - which is an ordinary answer rather
    /// than a failure, exactly as a missing file is on the Contents API.</para>
    /// </summary>
    /// <exception cref="GitHubApiException">GitHub refused the read for any other reason.</exception>
    public async Task<string?> GetBlobAsync(
        string credential, string owner, string repo, string sha, CancellationToken ct = default)
    {
        using var request = NewRequest(
            HttpMethod.Get, $"{RepoPath(owner, repo)}/git/blobs/{EscapePath(sha)}", credential);
        // Per-request, so the client's default application/vnd.github+json stays
        // right for every other call.
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(RawBlobMediaType));

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("GitHub has no blob {Sha} in {Owner}/{Repo}.", sha, owner, repo);
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var (message, url) = ReadError(Encoding.UTF8.GetString(bytes));
            _logger.LogWarning(
                "GitHub refused to serve blob {Sha} in {Owner}/{Repo} with {Status}: {Message}",
                sha, owner, repo, (int)response.StatusCode, message);
            throw new GitHubApiException(response.StatusCode, message, url);
        }

        _logger.LogDebug(
            "Read blob {Sha} from {Owner}/{Repo}: {ByteCount} bytes.", sha, owner, repo, bytes.Length);
        return DecodeUtf8(bytes);
    }

    /// <summary>
    /// UTF-8 text from raw bytes, without the byte-order mark GitHub passes
    /// through from whatever committed the file.
    /// </summary>
    private static string DecodeUtf8(byte[] bytes)
    {
        const char ByteOrderMark = '\uFEFF';
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == ByteOrderMark ? text[1..] : text;
    }
}
