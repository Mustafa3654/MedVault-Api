using System.Collections.Concurrent;
using System.Security.Cryptography;
using MedVault.Api.Data;

namespace MedVault.Api.Auth;

public interface ITokenService
{
    string Issue(HisAccount account);
    HisAccount? Validate(string? token);
    void Revoke(string? token);
}

/// <summary>
/// Opaque random tokens, 40 bytes rendered as 80 lowercase hex characters to match the MedVault format.
///
/// Storage is in-process, which means two things worth knowing before deployment:
///   - Every token is invalidated by an app restart or an IIS app-pool recycle; the app must handle
///     a 401 by re-logging in rather than showing an error.
///   - It does not work behind a load balancer with more than one instance. If the hospital runs more
///     than one node, swap this for a distributed cache or a signed token; ITokenService is the seam.
///
/// The token itself is never stored - only its SHA-256 - so a memory dump does not yield usable
/// credentials.
/// </summary>
public sealed class InMemoryTokenService(IConfiguration config, TimeProvider clock) : ITokenService
{
    private sealed record Entry(HisAccount Account, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);

    private readonly TimeSpan _lifetime =
        TimeSpan.FromMinutes(config.GetValue("Auth:TokenLifetimeMinutes", 720));  // spec section 3, item 3

    public string Issue(HisAccount account)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(40));
        _tokens[Fingerprint(token)] = new Entry(account, clock.GetUtcNow().Add(_lifetime));
        Sweep();
        return token;
    }

    public HisAccount? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (!_tokens.TryGetValue(Fingerprint(token), out var entry)) return null;

        if (entry.ExpiresAt <= clock.GetUtcNow())
        {
            _tokens.TryRemove(Fingerprint(token), out _);
            return null;
        }

        return entry.Account;
    }

    public void Revoke(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        _tokens.TryRemove(Fingerprint(token), out _);
    }

    private static string Fingerprint(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var (key, entry) in _tokens)
            if (entry.ExpiresAt <= now)
                _tokens.TryRemove(key, out _);
    }
}
