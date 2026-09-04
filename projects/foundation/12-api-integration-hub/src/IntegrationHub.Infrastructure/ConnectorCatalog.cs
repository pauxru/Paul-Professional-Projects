using IntegrationHub.Application;
using IntegrationHub.Domain;

namespace IntegrationHub.Infrastructure;

public static class ConnectorCatalog
{
    public static IReadOnlyList<IConnector> Create(
        ConnectorHostOptions hosts,
        IHttpClientFactory clients,
        SecretReferenceResolver secrets,
        ConnectorUrlGuard guard,
        IClock clock,
        FileConnector file,
        WebhookSourceConnector webhook)
    {
        return
        [
            BuildCrm(hosts, clients.CreateClient("connectors"), secrets, guard, clock),
            BuildErp(hosts, clients.CreateClient("connectors"), secrets, guard, clock),
            BuildPayments(hosts, clients.CreateClient("connectors"), secrets, guard, clock),
            file,
            webhook
        ];
    }

    public static RestConnector BuildCrm(
        ConnectorHostOptions hosts,
        HttpClient client,
        SecretReferenceResolver secrets,
        ConnectorUrlGuard guard,
        IClock clock)
    {
        var contact = new JsonContract("crm-contact", [
            new ContractField("$.id", ContractValueType.String),
            new ContractField("$.displayName", ContractValueType.String),
            new ContractField("$.email", ContractValueType.String, ContainsPii: true)
        ], false);
        var operations = new[]
        {
            new ConnectorOperationDescriptor("contacts.list", "GET", "/api/contacts", null, contact, true),
            new ConnectorOperationDescriptor("contacts.get", "GET", "/api/contacts/{id}", null, contact, true),
            new ConnectorOperationDescriptor("contacts.upsert", "POST", "/api/contacts", contact, contact, true),
            new ConnectorOperationDescriptor("accounts.list", "GET", "/api/accounts", null, null, true),
            new ConnectorOperationDescriptor("opportunities.list", "GET", "/api/opportunities", null, null, true)
        };
        var descriptor = new ConnectorDescriptor(
            "contoso-crm", "Contoso CRM", "1.0.0", ConnectorAuthKind.ApiKey, operations,
            new RateLimitDescriptor(100, TimeSpan.FromMinutes(1), 4), PaginationStyle.PageNumber,
            "Contacts, accounts and opportunities from the fictional Contoso CRM.");
        return new RestConnector(client, new RestConnectorOptions(
            descriptor,
            new Uri(hosts.CrmBaseUrl),
            new RestAuthenticationOptions(ConnectorAuthKind.ApiKey, "X-Api-Key", "@secret:crm/apiKey"),
            new Dictionary<string, RestOperationOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["contacts.list"] = new("contacts.list", "GET", "/api/contacts", ResponsePath: "$.items", PaginationStyle: PaginationStyle.PageNumber),
                ["contacts.get"] = new("contacts.get", "GET", "/api/contacts/{id}"),
                ["contacts.upsert"] = new("contacts.upsert", "POST", "/api/contacts", ErrorMapping: new Dictionary<int, string> { [422] = "CRM contact validation failed." }),
                ["accounts.list"] = new("accounts.list", "GET", "/api/accounts", ResponsePath: "$.items", PaginationStyle: PaginationStyle.Offset),
                ["opportunities.list"] = new("opportunities.list", "GET", "/api/opportunities", ResponsePath: "$.items", PaginationStyle: PaginationStyle.Cursor)
            }), secrets, guard, clock);
    }

    public static RestConnector BuildErp(
        ConnectorHostOptions hosts,
        HttpClient client,
        SecretReferenceResolver secrets,
        ConnectorUrlGuard guard,
        IClock clock)
    {
        var customer = new JsonContract("erp-customer", [
            new ContractField("$.id", ContractValueType.String),
            new ContractField("$.name", ContractValueType.String),
            new ContractField("$.currency", ContractValueType.String)
        ], false);
        var operationDescriptors = new[]
        {
            new ConnectorOperationDescriptor("customers.list", "GET", "/api/customers", null, customer, true),
            new ConnectorOperationDescriptor("customers.upsert", "POST", "/api/customers", customer, customer, true),
            new ConnectorOperationDescriptor("products.list", "GET", "/api/products", null, null, true),
            new ConnectorOperationDescriptor("sales-orders.create", "POST", "/api/sales-orders", null, null, true),
            new ConnectorOperationDescriptor("invoices.list", "GET", "/api/invoices", null, null, true)
        };
        var descriptor = new ConnectorDescriptor(
            "acme-erp", "Acme ERP", "1.0.0", ConnectorAuthKind.Basic, operationDescriptors,
            new RateLimitDescriptor(60, TimeSpan.FromMinutes(1), 3), PaginationStyle.Offset,
            "Customers, products, sales orders and invoices in the fictional Acme ERP.");
        return new RestConnector(client, new RestConnectorOptions(
            descriptor,
            new Uri(hosts.ErpBaseUrl),
            new RestAuthenticationOptions(ConnectorAuthKind.Basic, Username: "@secret:erp/username", Password: "@secret:erp/password"),
            new Dictionary<string, RestOperationOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["customers.list"] = new("customers.list", "GET", "/api/customers", ResponsePath: "$.items", PaginationStyle: PaginationStyle.Offset),
                ["customers.upsert"] = new("customers.upsert", "POST", "/api/customers", ErrorMapping: new Dictionary<int, string> { [409] = "ERP customer version conflict.", [422] = "ERP customer validation failed." }),
                ["products.list"] = new("products.list", "GET", "/api/products", ResponsePath: "$.items", PaginationStyle: PaginationStyle.LinkHeader),
                ["sales-orders.create"] = new("sales-orders.create", "POST", "/api/sales-orders"),
                ["invoices.list"] = new("invoices.list", "GET", "/api/invoices", ResponsePath: "$.items", PaginationStyle: PaginationStyle.PageNumber)
            }), secrets, guard, clock);
    }

    public static RestConnector BuildPayments(
        ConnectorHostOptions hosts,
        HttpClient client,
        SecretReferenceResolver secrets,
        ConnectorUrlGuard guard,
        IClock clock)
    {
        var charge = new JsonContract("payment-charge", [
            new ContractField("$.id", ContractValueType.String),
            new ContractField("$.amount", ContractValueType.Number),
            new ContractField("$.currency", ContractValueType.String)
        ]);
        var operationDescriptors = new[]
        {
            new ConnectorOperationDescriptor("charges.create", "POST", "/api/charges", charge, charge, true),
            new ConnectorOperationDescriptor("refunds.create", "POST", "/api/refunds", null, null, true),
            new ConnectorOperationDescriptor("settlements.list", "GET", "/api/settlements", null, null, true)
        };
        var descriptor = new ConnectorDescriptor(
            "pesagate-payments", "PesaGate Payments (fictional)", "1.0.0", ConnectorAuthKind.OAuth2ClientCredentials,
            operationDescriptors, new RateLimitDescriptor(30, TimeSpan.FromMinutes(1), 2), PaginationStyle.Cursor,
            "Charges, refunds and settlements for the fictional PesaGate provider.");
        return new RestConnector(client, new RestConnectorOptions(
            descriptor,
            new Uri(hosts.PaymentsBaseUrl),
            new RestAuthenticationOptions(
                ConnectorAuthKind.OAuth2ClientCredentials,
                TokenEndpoint: "/oauth/token",
                ClientId: "@secret:payments/clientId",
                ClientSecret: "@secret:payments/clientSecret",
                Scope: "payments.write settlements.read"),
            new Dictionary<string, RestOperationOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["charges.create"] = new("charges.create", "POST", "/api/charges", ErrorMapping: new Dictionary<int, string> { [402] = "Payment was declined." }),
                ["refunds.create"] = new("refunds.create", "POST", "/api/refunds"),
                ["settlements.list"] = new("settlements.list", "GET", "/api/settlements", ResponsePath: "$.items", PaginationStyle: PaginationStyle.Cursor)
            }), secrets, guard, clock);
    }
}
