using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CloudAgent.Core.Tools.Web;

/// <summary>A fetched resource: where it ended up, what it is, and its decoded text.</summary>
/// <param name="Url">The final URL after redirects.</param>
/// <param name="MediaType">The response media type, lowercased.</param>
/// <param name="Body">The decoded body.</param>
/// <param name="Truncated">Whether the body was cut at the size limit.</param>
public sealed record FetchedDocument(Uri Url, string MediaType, string Body, bool Truncated);

/// <summary>A fetch the fetcher refused or could not complete, with a reason fit to show the agent.</summary>
public sealed class WebFetchException(string message) : Exception(message);

/// <summary>
/// Fetches web pages on the agent's behalf, politely and safely: an honest user agent, robots.txt,
/// a per-host request interval, size and time limits, and no connections to loopback, private, or
/// link-local addresses (checked at connect time, so redirects and DNS tricks cannot bypass it).
/// </summary>
public sealed class WebFetcher
{
    public const string UserAgent = "CloudAgent/0.1 (+https://github.com/dcancho/cloudagent)";

    private const int MaxRedirects = 5;
    private const int MaxBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan HostInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RobotsTtl = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, RobotsRules Rules)> _robots = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRequest = new();

    /// <param name="handler">Override the transport (tests). The default enforces public addresses only.</param>
    public WebFetcher(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? CreatePublicOnlyHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5");
    }

    public async Task<FetchedDocument> FetchAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var current = url;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            EnsureHttp(current);
            if (!await IsAllowedByRobotsAsync(current, cancellationToken).ConfigureAwait(false))
                throw new WebFetchException($"robots.txt on {current.Host} disallows fetching {current.AbsolutePath}.");

            using var response = await SendPacedAsync(current, cancellationToken).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location
                    ?? throw new WebFetchException($"{current} redirected without a location.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new WebFetchException($"{current} returned HTTP {(int)response.StatusCode}.");

            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "text/html";
            if (!IsTextual(mediaType))
                throw new WebFetchException($"{current} is {mediaType}, which is not a readable text format.");

            var (bytes, truncated) = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var body = Decode(bytes, response.Content.Headers.ContentType);
            return new FetchedDocument(current, mediaType, body, truncated);
        }

        throw new WebFetchException($"Too many redirects starting from {url}.");
    }

    /// <summary>Whether an address is on the public internet (not loopback, private, link-local, or reserved).</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0
                || b[0] == 10
                || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || b[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC);
        }

        return false;
    }

    private static SocketsHttpHandler CreatePublicOnlyHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
                .ConfigureAwait(false);
            var allowed = addresses.Where(IsPublicAddress).ToArray();
            if (allowed.Length == 0)
                throw new WebFetchException($"{context.DnsEndPoint.Host} does not resolve to a public address.");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    private async Task<HttpResponseMessage> SendPacedAsync(Uri url, CancellationToken cancellationToken)
    {
        var gate = _hostGates.GetOrAdd(url.Host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastRequest.TryGetValue(url.Host, out var last))
            {
                var wait = last + HostInterval - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return await _http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.InnerException is WebFetchException blocked)
            {
                throw blocked;
            }
            catch (HttpRequestException ex)
            {
                throw new WebFetchException($"Could not reach {url.Host}: {ex.Message}");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WebFetchException($"{url} timed out.");
            }
        }
        finally
        {
            _lastRequest[url.Host] = DateTimeOffset.UtcNow;
            gate.Release();
        }
    }

    private async Task<bool> IsAllowedByRobotsAsync(Uri url, CancellationToken cancellationToken)
    {
        var key = url.GetLeftPart(UriPartial.Authority);
        if (!_robots.TryGetValue(key, out var cached) || DateTimeOffset.UtcNow - cached.At > RobotsTtl)
        {
            cached = (DateTimeOffset.UtcNow, await LoadRobotsAsync(new Uri(key + "/robots.txt"), cancellationToken).ConfigureAwait(false));
            _robots[key] = cached;
        }

        return cached.Rules.IsAllowed(url.PathAndQuery);
    }

    private async Task<RobotsRules> LoadRobotsAsync(Uri robotsUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendPacedAsync(robotsUrl, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500)
                return RobotsRules.DisallowAll;
            if (!response.IsSuccessStatusCode)
                return RobotsRules.AllowAll;

            var (bytes, _) = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return RobotsRules.Parse(Encoding.UTF8.GetString(bytes), "cloudagent");
        }
        catch (WebFetchException)
        {
            return RobotsRules.AllowAll;
        }
    }

    private static async Task<(byte[] Bytes, bool Truncated)> ReadLimitedAsync(
        HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var room = MaxBytes - (int)buffer.Length;
            if (read >= room)
            {
                buffer.Write(chunk, 0, room);
                return (buffer.ToArray(), true);
            }
            buffer.Write(chunk, 0, read);
        }
        return (buffer.ToArray(), false);
    }

    private static string Decode(byte[] bytes, MediaTypeHeaderValue? contentType)
    {
        var encoding = Encoding.UTF8;
        if (contentType?.CharSet is { Length: > 0 } charset)
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
            }
        }
        return encoding.GetString(bytes);
    }

    private static void EnsureHttp(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            throw new WebFetchException($"Only http and https URLs can be fetched, not {url.Scheme}.");
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsTextual(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.Ordinal)
        || mediaType is "application/xhtml+xml" or "application/json" or "application/xml";
}

/// <summary>The subset of robots.txt that matters to a single agent: allow and disallow rules for its group.</summary>
public sealed class RobotsRules
{
    private readonly IReadOnlyList<(Regex Pattern, int Length, bool Allow)> _rules;

    private RobotsRules(IReadOnlyList<(Regex, int, bool)> rules) => _rules = rules;

    public static RobotsRules AllowAll { get; } = new([]);

    public static RobotsRules DisallowAll { get; } = new([(new Regex("^/"), 1, false)]);

    /// <summary>Parse robots.txt, using the group for <paramref name="agentToken"/> if present, else <c>*</c>.</summary>
    public static RobotsRules Parse(string text, string agentToken)
    {
        var groups = new List<(List<string> Agents, List<(string Path, bool Allow)> Rules)>();
        (List<string> Agents, List<(string, bool)> Rules)? current = null;
        var lastWasAgent = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Split('#')[0].Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (field == "user-agent")
            {
                if (current is null || !lastWasAgent)
                {
                    current = (new List<string>(), new List<(string, bool)>());
                    groups.Add(current.Value);
                }
                current.Value.Agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
                continue;
            }

            lastWasAgent = false;
            if (current is null || (field != "allow" && field != "disallow"))
                continue;
            if (field == "disallow" && value.Length == 0)
                continue;
            current.Value.Rules.Add((value, field == "allow"));
        }

        var token = agentToken.ToLowerInvariant();
        var chosen = groups.Where(g => g.Agents.Any(a => a != "*" && token.Contains(a))).ToList();
        if (chosen.Count == 0)
            chosen = groups.Where(g => g.Agents.Contains("*")).ToList();

        var rules = chosen
            .SelectMany(g => g.Rules)
            .Select(r => (ToRegex(r.Path), r.Path.Length, r.Allow))
            .ToList();
        return new RobotsRules(rules);
    }

    /// <summary>Longest matching rule wins; on a tie, allow wins. No match means allowed.</summary>
    public bool IsAllowed(string pathAndQuery)
    {
        (int Length, bool Allow)? best = null;
        foreach (var (pattern, length, allow) in _rules)
        {
            if (!pattern.IsMatch(pathAndQuery))
                continue;
            if (best is null || length > best.Value.Length || (length == best.Value.Length && allow))
                best = (length, allow);
        }
        return best?.Allow ?? true;
    }

    private static Regex ToRegex(string path)
    {
        var anchored = path.EndsWith('$');
        var body = anchored ? path[..^1] : path;
        var pattern = "^" + Regex.Escape(body).Replace("\\*", ".*") + (anchored ? "$" : "");
        return new Regex(pattern, RegexOptions.CultureInvariant);
    }
}
