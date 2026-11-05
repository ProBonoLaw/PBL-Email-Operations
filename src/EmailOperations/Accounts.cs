using EmailOperations.Core;
using Microsoft.Azure.Cosmos;

namespace EmailOperations;

public sealed class AccountDetails
{
    public string? AccountId { get; set; }
    public string? CaseNumber { get; set; }
    public string? IngestionServiceAccount { get; set; }
    public PersonDetails? PersonInformation { get; set; }
    public List<AttorneyDetails>? AttorneyInformation { get; set; }
}

public sealed class PersonDetails
{
    public string? FirstName { get; set; }
    public string? PreferredName { get; set; }
    public string? EmailAddress { get; set; }
}

public sealed class AttorneyDetails
{
    public string? EmailAddress { get; set; }
}

public interface IAccountRepository
{
    Task<IReadOnlyList<AccountDetails>> GetActiveAsync(CancellationToken token);
    Task<AccountDetails> GetAsync(string accountId, CancellationToken token);
    Task<string?> GetBillingCodeAsync(string accountId, CancellationToken token);
}

public sealed class AccountRepository : IAccountRepository, IDisposable
{
    private sealed class BillingRow { public string? BillingCode { get; set; } }
    private readonly CosmosClient client = new(Settings.Required("COSMOS_CONNECTION_STRING"));
    private Container Container => client.GetContainer(Settings.Get("COSMOS_DATABASE") ??
        "db_conversation_history", Settings.Get("COSMOS_CONTAINER") ?? "accounts");

    public async Task<IReadOnlyList<AccountDetails>> GetActiveAsync(CancellationToken token)
    {
        var query = new QueryDefinition("SELECT c.accountId, c.caseNumber, c.ingestionServiceAccount, " +
            "c.personInformation, c.attorneyInformation FROM c WHERE c.type = 'ACCOUNT_DETAILS' " +
            "AND IS_DEFINED(c.ingestionServiceAccount) AND NOT IS_NULL(c.ingestionServiceAccount)");
        var items = new List<AccountDetails>();
        using var iterator = Container.GetItemQueryIterator<AccountDetails>(query);
        while (iterator.HasMoreResults)
            items.AddRange(await iterator.ReadNextAsync(token));
        return items;
    }

    public async Task<AccountDetails> GetAsync(string accountId, CancellationToken token)
    {
        var query = new QueryDefinition("SELECT TOP 2 c.accountId, c.caseNumber, c.ingestionServiceAccount, " +
            "c.personInformation, c.attorneyInformation FROM c WHERE c.type = 'ACCOUNT_DETAILS' " +
            "AND c.accountId = @accountId").WithParameter("@accountId", accountId);
        using var iterator = Container.GetItemQueryIterator<AccountDetails>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(accountId) });
        var results = new List<AccountDetails>();
        while (iterator.HasMoreResults) results.AddRange(await iterator.ReadNextAsync(token));
        return results.Count == 1 ? results[0] : throw new InvalidDataException(
            $"Expected one account details document for {accountId}; found {results.Count}");
    }

    public async Task<string?> GetBillingCodeAsync(string accountId, CancellationToken token)
    {
        var query = new QueryDefinition("SELECT TOP 1 caseAccount.billingCode AS billingCode " +
            "FROM c JOIN caseAccount IN c.casesAccounts WHERE c.accountId = " +
            "'00000000-0000-0000-0000-000000000000' AND caseAccount.accountId = @accountId " +
            "AND IS_DEFINED(caseAccount.billingCode)").WithParameter("@accountId", accountId);
        using var iterator = Container.GetItemQueryIterator<BillingRow>(query);
        if (!iterator.HasMoreResults) return null;
        var results = await iterator.ReadNextAsync(token);
        return results.Count == 0 ? null : results.First().BillingCode;
    }

    public void Dispose() => client.Dispose();
}
