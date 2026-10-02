using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using Activout.Banking.Archive;
using Activout.Banking.EnableBanking;

namespace Activout.Banking;

/// <summary>One in-process authorisation attempt. Single use; expires after <see cref="Authoriser.AttemptLifetime"/>.</summary>
public sealed class PendingAuthorisation
{
    public required string Connection { get; init; }
    public required string Bank { get; init; }
    public required string Country { get; init; }
    public required string PsuType { get; init; }
    public required string State { get; init; }
    public required Uri RedirectUri { get; init; }
    public required Uri LocalCallbackUri { get; init; }

    /// <summary>Bank authorisation URL for the owner's browser.</summary>
    public required string Url { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
    public bool Consumed { get; internal set; }
}

public sealed record AuthorisationResult(Connection Connection, IReadOnlyList<Account> Accounts);

/// <summary>Runs the account information consent flow: /aspsps, /auth, callback validation and /sessions.</summary>
public sealed class Authoriser(BankingClient client, BankArchive archive, BankConfig config, TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<PendingAuthorisation> Begin(string connectionName, string bank, string country, string psuType,
        CancellationToken ct)
    {
        psuType = PsuTypes.Parse(psuType);
        country = country.ToUpperInvariant();

        var existing = await archive.Dao.GetConnection(connectionName);
        if (existing != null && (!existing.AspspName.Equals(bank, StringComparison.OrdinalIgnoreCase) ||
                                 existing.AspspCountry != country || existing.PsuType != psuType))
        {
            throw new BankingUsageException(
                $"Connection '{connectionName}' is {existing.AspspName} ({existing.AspspCountry}, {existing.PsuType}); " +
                "choose another name for a different bank or PSU type");
        }

        var aspsps = await client.GetAspsps(country, psuType, ct);
        var aspsp = aspsps.FirstOrDefault(a => a.Name.Equals(bank, StringComparison.OrdinalIgnoreCase) &&
                                               (a.PsuTypes == null || a.PsuTypes.Contains(psuType)));
        if (aspsp == null)
        {
            var similar = aspsps.Where(a => a.Name.Contains(bank, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Name).ToList();
            throw new BankingUsageException(
                $"'{bank}' does not offer {psuType} account information in {country} through Enable Banking" +
                (similar.Count > 0 ? $". Similar names: {string.Join(", ", similar)}" : ""));
        }

        var now = _time.GetUtcNow();
        var state = JwtSigner.Base64Url(RandomNumberGenerator.GetBytes(32));
        var response = await client.StartAuthorisation(new StartAuthorisationRequest(
            new Access(now.AddSeconds(aspsp.MaximumConsentValidity)),
            new AspspReference(aspsp.Name, aspsp.Country),
            state,
            config.RedirectUrl,
            psuType), ct);

        return new PendingAuthorisation
        {
            Connection = connectionName,
            Bank = aspsp.Name,
            Country = aspsp.Country,
            PsuType = psuType,
            State = state,
            RedirectUri = config.RedirectUri,
            LocalCallbackUri = config.LocalCallbackUri,
            Url = response.Url,
            ExpiresAt = now + AttemptLifetime,
        };
    }

    /// <summary>
    /// Validates a callback URL against the pending attempt and consumes it. Returns the authorisation code.
    /// Accepts either the registered redirect URL or the local listener URL that a forwarder targets.
    /// </summary>
    public string ValidateCallback(PendingAuthorisation pending, Uri callback)
    {
        if (pending.Consumed)
            throw new AuthorisationException("This authorisation attempt has already been completed");
        if (_time.GetUtcNow() > pending.ExpiresAt)
            throw new AuthorisationException("The authorisation attempt has expired; run 'bank connect' again");
        if (!SameEndpoint(callback, pending.RedirectUri) && !SameEndpoint(callback, pending.LocalCallbackUri))
            throw new AuthorisationException(
                $"Callback went to {callback.GetLeftPart(UriPartial.Path)}, expected {pending.RedirectUri.GetLeftPart(UriPartial.Path)}");

        var query = HttpUtility.ParseQueryString(callback.Query);
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(query["state"] ?? ""),
                System.Text.Encoding.UTF8.GetBytes(pending.State)))
        {
            throw new AuthorisationException("Callback state does not match this authorisation attempt");
        }

        // A matching state ends this attempt, whatever the outcome.
        pending.Consumed = true;

        if (query["error"] is { } error)
        {
            throw new AuthorisationException(
                $"Bank authorisation failed: {error}{(query["error_description"] is { } d ? $" ({d})" : "")}");
        }

        return query["code"] is { Length: > 0 } code
            ? code
            : throw new AuthorisationException("Callback contains no authorisation code");
    }

    /// <summary>Exchanges the code for a session and stores the connection and its accounts.</summary>
    public async Task<AuthorisationResult> Complete(PendingAuthorisation pending, string code, CancellationToken ct)
    {
        var session = await client.CreateSession(code, ct);
        var now = BankArchive.Timestamp(_time.GetUtcNow());
        long connectionId = 0;

        await archive.InTransaction(async () =>
        {
            var row = await archive.Dao.GetConnection(pending.Connection);
            connectionId = row?.Id ?? await archive.Dao.InsertConnection(pending.Connection, pending.Bank,
                pending.Country, pending.PsuType, now);
            await archive.Dao.UpdateConnectionSession(connectionId, session.SessionId,
                session.Access == null ? null : BankArchive.Timestamp(session.Access.ValidUntil), now);
            await ReconcileAccounts(connectionId, session.Accounts, now);
        });

        var connection = (await archive.Dao.GetConnectionById(connectionId))!;
        var accounts = (await archive.Dao.ListAccounts(connectionId))
            .Where(a => a.ProviderUid != null)
            .Select(a => Account.From(a, connection.Name)).ToList();
        return new AuthorisationResult(Connection.From(connection), accounts);
    }

    /// <summary>
    /// Provider account UIDs change with every session. Existing accounts are matched by identification hash or
    /// account identification plus currency within the connection, keeping local IDs, aliases and history.
    /// </summary>
    internal async Task ReconcileAccounts(long connectionId, IReadOnlyList<JsonElement> received, string now)
    {
        var existing = (await archive.Dao.ListAccounts(connectionId)).ToList();
        var matched = new HashSet<long>();
        await archive.Dao.ClearProviderUids(connectionId, now);

        foreach (var raw in received)
        {
            var resource = raw.Deserialize<AccountResource>(Json.Options)!;
            var hashes = (resource.IdentificationHashes ?? []).Append(resource.IdentificationHash)
                .Where(h => !string.IsNullOrEmpty(h)).ToHashSet();
            var iban = resource.AccountId?.Iban;
            var other = resource.AccountId?.Other?.Identification;

            var candidates = existing.Where(a => !matched.Contains(a.Id) &&
                                                 (a.Currency == null || resource.Currency == null ||
                                                  a.Currency == resource.Currency) &&
                                                 (JsonSerializer.Deserialize<string[]>(a.IdentificationHashes)!
                                                      .Any(hashes.Contains) ||
                                                  (iban != null && a.Iban == iban) ||
                                                  (other != null && a.OtherIdentification == other)))
                .ToList();

            if (candidates.Count > 1)
            {
                throw new BankingUsageException(
                    $"Received account {iban ?? other ?? resource.Uid} matches local accounts " +
                    $"{string.Join(", ", candidates.Select(c => c.Id))}; refusing to merge them automatically");
            }

            var row = new AccountRow
            {
                Id = candidates.Count == 1 ? candidates[0].Id : 0,
                ConnectionId = connectionId,
                ProviderUid = resource.Uid,
                IdentificationHash = resource.IdentificationHash,
                IdentificationHashes = JsonSerializer.Serialize(hashes.Order().ToArray()),
                Iban = iban,
                OtherIdentification = other,
                Currency = resource.Currency,
                Name = resource.Name,
                Details = resource.Details,
                Product = resource.Product,
                RawJson = raw.GetRawText(),
                CreatedAt = now,
                UpdatedAt = now,
            };

            if (candidates.Count == 1)
            {
                matched.Add(row.Id);
                await archive.Dao.UpdateAccountFromProvider(row);
            }
            else
            {
                matched.Add(await archive.Dao.InsertAccount(row));
            }
        }
    }

    private static bool SameEndpoint(Uri a, Uri b) =>
        a.Scheme.Equals(b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) &&
        a.Port == b.Port &&
        a.AbsolutePath == b.AbsolutePath;
}
