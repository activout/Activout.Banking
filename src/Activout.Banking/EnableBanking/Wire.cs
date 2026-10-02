using System.Text.Json;
using System.Text.Json.Serialization;

namespace Activout.Banking.EnableBanking;

// Enable Banking wire DTOs. Only the fields this library uses are mapped; raw JSON is kept where the
// archive needs the full received object. Property names map through the snake_case naming policy.

public sealed record ApplicationInfo(
    string Name,
    string? Kid,
    string? Environment,
    IReadOnlyList<string>? RedirectUrls,
    bool Active,
    IReadOnlyList<string>? Services);

public sealed record AspspList(IReadOnlyList<Aspsp> Aspsps);

public sealed record Aspsp(
    string Name,
    string Country,
    IReadOnlyList<string>? PsuTypes,
    long MaximumConsentValidity,
    bool Beta);

public sealed record AspspReference(string Name, string Country);

public sealed record Access(DateTimeOffset ValidUntil);

public sealed record StartAuthorisationRequest(
    Access Access,
    AspspReference Aspsp,
    string State,
    string RedirectUrl,
    string PsuType);

public sealed record StartAuthorisationResponse(string Url, string? AuthorizationId);

public sealed record CreateSessionRequest(string Code);

public sealed record CreateSessionResponse(
    string SessionId,
    IReadOnlyList<JsonElement> Accounts,
    AspspReference? Aspsp,
    string? PsuType,
    Access? Access);

public sealed record AccountIdentification(string? Iban, GenericIdentification? Other);

public sealed record GenericIdentification(string? Identification, string? SchemeName);

public sealed record AccountResource(
    string? Uid,
    AccountIdentification? AccountId,
    string? IdentificationHash,
    IReadOnlyList<string>? IdentificationHashes,
    string? Name,
    string? Details,
    string? Product,
    string? Currency);

public sealed record SessionInfo(
    string? Status,
    IReadOnlyList<string>? Accounts,
    Access? Access);

public sealed record AmountType(string Amount, string Currency);

public sealed record BalanceList(IReadOnlyList<BalanceResource> Balances);

public sealed record BalanceResource(
    string? Name,
    AmountType BalanceAmount,
    string? BalanceType,
    string? ReferenceDate,
    DateTimeOffset? LastChangeDateTime);

public sealed record TransactionPage(IReadOnlyList<JsonElement>? Transactions, string? ContinuationKey);

public sealed record PartyIdentification(string? Name);

public sealed record WireTransaction(
    string? EntryReference,
    string? TransactionId,
    AmountType TransactionAmount,
    PartyIdentification? Creditor,
    AccountIdentification? CreditorAccount,
    PartyIdentification? Debtor,
    AccountIdentification? DebtorAccount,
    string? CreditDebitIndicator,
    string? Status,
    string? BookingDate,
    string? ValueDate,
    string? TransactionDate,
    string? ReferenceNumber,
    IReadOnlyList<string>? RemittanceInformation,
    string? Note);

public sealed record ErrorResponse(string? Code, string? Message, string? Error, JsonElement? Detail);

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
