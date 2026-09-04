using Microsoft.Data.Sqlite;
using Northstar.Legacy.Web.Models;

namespace Northstar.Legacy.Web.Legacy;

public static class DatabaseHelper
{
    private static readonly Dictionary<string, List<LegacyClaim>> ClaimCache = new(StringComparer.OrdinalIgnoreCase);

    public static void InitializeSchema(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Policyholders (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Email TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Policies (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PolicyNumber TEXT NOT NULL UNIQUE,
                PolicyholderId INTEGER NOT NULL,
                Deductible REAL NOT NULL,
                PolicyLimit REAL NOT NULL,
                Currency TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Claims (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ClaimReference TEXT NOT NULL UNIQUE,
                PolicyId INTEGER NOT NULL,
                PolicyholderName TEXT NOT NULL,
                Status TEXT NOT NULL,
                ClaimedAmount REAL NOT NULL,
                Deductible REAL NOT NULL,
                PolicyLimit REAL NOT NULL,
                ReserveAmount REAL NOT NULL DEFAULT 0,
                Currency TEXT NOT NULL,
                Adjuster TEXT NULL,
                CreatedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ClaimDocuments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ClaimId INTEGER NOT NULL,
                OriginalName TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                UploadedUtc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public static void SeedDemoData(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Claims;";
        if (Convert.ToInt32(count.ExecuteScalar()) > 0)
        {
            return;
        }

        Execute(connection, "INSERT INTO Policyholders (Name, Email) VALUES (@name, @email);", ("@name", "Acme Manufacturing (fictional)"), ("@email", "claims@acme.example"));
        Execute(connection, "INSERT INTO Policyholders (Name, Email) VALUES (@name, @email);", ("@name", "Contoso Retail (fictional)"), ("@email", "claims@contoso.example"));
        Execute(connection, "INSERT INTO Policies (PolicyNumber, PolicyholderId, Deductible, PolicyLimit, Currency) VALUES ('POL-ACME-001', 1, 500, 10000, 'USD');");
        Execute(connection, "INSERT INTO Policies (PolicyNumber, PolicyholderId, Deductible, PolicyLimit, Currency) VALUES ('POL-CONTOSO-001', 2, 250, 5000, 'USD');");
        Execute(connection, "INSERT INTO Claims (ClaimReference, PolicyId, PolicyholderName, Status, ClaimedAmount, Deductible, PolicyLimit, ReserveAmount, Currency, CreatedUtc) VALUES ('CLM-1001', 1, 'Acme Manufacturing (fictional)', 'Submitted', 3400, 500, 10000, 3400, 'USD', @created);", ("@created", DateTime.UtcNow.ToString("O")));
        Execute(connection, "INSERT INTO Claims (ClaimReference, PolicyId, PolicyholderName, Status, ClaimedAmount, Deductible, PolicyLimit, ReserveAmount, Currency, CreatedUtc) VALUES ('CLM-1002', 2, 'Contoso Retail (fictional)', 'UnderReview', 7200, 250, 5000, 5000, 'USD', @created);", ("@created", DateTime.UtcNow.ToString("O")));
    }

    public static LegacyPolicy? FindPolicy(string connectionString, string policyNumber)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.Id, p.PolicyNumber, h.Name, p.Deductible, p.PolicyLimit, p.Currency
            FROM Policies p JOIN Policyholders h ON h.Id = p.PolicyholderId
            WHERE p.PolicyNumber = @number;
            """;
        command.Parameters.AddWithValue("@number", policyNumber);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPolicy(reader) : null;
    }

    public static LegacyClaim? FindClaim(string connectionString, long claimId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ClaimReference, PolicyId, PolicyholderName, Status, ClaimedAmount, Deductible, PolicyLimit, ReserveAmount, Currency, Adjuster, CreatedUtc FROM Claims WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", claimId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadClaim(reader) : null;
    }

    public static List<LegacyPolicyholder> GetPolicyholders(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Email FROM Policyholders ORDER BY Name;";
        using var reader = command.ExecuteReader();
        var policyholders = new List<LegacyPolicyholder>();
        while (reader.Read())
        {
            policyholders.Add(new LegacyPolicyholder
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                Email = reader.GetString(2)
            });
        }

        return policyholders;
    }

    public static List<LegacyPolicy> GetPolicies(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.Id, p.PolicyNumber, h.Name, p.Deductible, p.PolicyLimit, p.Currency
            FROM Policies p JOIN Policyholders h ON h.Id = p.PolicyholderId
            ORDER BY p.PolicyNumber;
            """;
        using var reader = command.ExecuteReader();
        var policies = new List<LegacyPolicy>();
        while (reader.Read())
        {
            policies.Add(ReadPolicy(reader));
        }

        return policies;
    }

    public static long InsertPolicyholder(string connectionString, string name, string email)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Policyholders (Name, Email) VALUES (@name, @email); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@email", email);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public static long InsertPolicy(string connectionString, string policyNumber, long policyholderId, decimal deductible, decimal policyLimit, string currency)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Policies (PolicyNumber, PolicyholderId, Deductible, PolicyLimit, Currency) VALUES (@number, @holderId, @deductible, @limit, @currency); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("@number", policyNumber);
        command.Parameters.AddWithValue("@holderId", policyholderId);
        command.Parameters.AddWithValue("@deductible", deductible);
        command.Parameters.AddWithValue("@limit", policyLimit);
        command.Parameters.AddWithValue("@currency", currency);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public static List<LegacyClaim> GetClaims(string connectionString)
    {
        if (ClaimCache.TryGetValue(connectionString, out var cached))
        {
            return cached.Select(Clone).ToList();
        }

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ClaimReference, PolicyId, PolicyholderName, Status, ClaimedAmount, Deductible, PolicyLimit, ReserveAmount, Currency, Adjuster, CreatedUtc FROM Claims ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var claims = new List<LegacyClaim>();
        while (reader.Read())
        {
            claims.Add(ReadClaim(reader));
        }

        ClaimCache[connectionString] = claims.Select(Clone).ToList();
        return claims;
    }

    public static List<LegacyClaim> FindClaimsByPolicyholderUnsafe(string connectionString, string policyholderName)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        // LEGACY-SMELL: user-controlled filter is concatenated into executable SQL. Kept for assessment/test only.
        command.CommandText = $"SELECT Id, ClaimReference, PolicyId, PolicyholderName, Status, ClaimedAmount, Deductible, PolicyLimit, ReserveAmount, Currency, Adjuster, CreatedUtc FROM Claims WHERE PolicyholderName LIKE '%{policyholderName}%' ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var claims = new List<LegacyClaim>();
        while (reader.Read())
        {
            claims.Add(ReadClaim(reader));
        }

        return claims;
    }

    public static long InsertClaim(string connectionString, LegacyClaim claim)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Claims (ClaimReference, PolicyId, PolicyholderName, Status, ClaimedAmount, Deductible, PolicyLimit, ReserveAmount, Currency, CreatedUtc)
            VALUES (@reference, @policyId, @holder, @status, @amount, @deductible, @limit, @reserve, @currency, @created);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@reference", claim.ClaimReference);
        command.Parameters.AddWithValue("@policyId", claim.PolicyId);
        command.Parameters.AddWithValue("@holder", claim.PolicyholderName);
        command.Parameters.AddWithValue("@status", claim.Status);
        command.Parameters.AddWithValue("@amount", claim.ClaimedAmount);
        command.Parameters.AddWithValue("@deductible", claim.Deductible);
        command.Parameters.AddWithValue("@limit", claim.PolicyLimit);
        command.Parameters.AddWithValue("@reserve", claim.ReserveAmount);
        command.Parameters.AddWithValue("@currency", claim.Currency);
        command.Parameters.AddWithValue("@created", claim.CreatedUtc.ToString("O"));
        var id = Convert.ToInt64(command.ExecuteScalar());
        ClaimCache.Remove(connectionString);
        return id;
    }

    public static void UpdateAssessment(string connectionString, long claimId, string adjuster, decimal reserveAmount, string status)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, "UPDATE Claims SET Adjuster=@adjuster, ReserveAmount=@reserve, Status=@status WHERE Id=@id;", ("@adjuster", adjuster), ("@reserve", reserveAmount), ("@status", status), ("@id", claimId));
        ClaimCache.Remove(connectionString);
    }

    public static void AddDocument(string connectionString, long claimId, string name, string path)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, "INSERT INTO ClaimDocuments (ClaimId, OriginalName, FilePath, UploadedUtc) VALUES (@id, @name, @path, @uploaded);", ("@id", claimId), ("@name", name), ("@path", path), ("@uploaded", DateTime.UtcNow.ToString("O")));
    }

    public static byte[] LoadDocumentBytes(string path)
    {
        // LEGACY-SMELL: data access utility reaches into local filesystem storage directly.
        return File.ReadAllBytes(path);
    }

    public static void ClearCache() => ClaimCache.Clear();

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static LegacyPolicy ReadPolicy(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        PolicyNumber = reader.GetString(1),
        PolicyholderName = reader.GetString(2),
        Deductible = reader.GetDecimal(3),
        PolicyLimit = reader.GetDecimal(4),
        Currency = reader.GetString(5)
    };

    private static LegacyClaim ReadClaim(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ClaimReference = reader.GetString(1),
        PolicyId = reader.GetInt64(2),
        PolicyholderName = reader.GetString(3),
        Status = reader.GetString(4),
        ClaimedAmount = reader.GetDecimal(5),
        Deductible = reader.GetDecimal(6),
        PolicyLimit = reader.GetDecimal(7),
        ReserveAmount = reader.GetDecimal(8),
        Currency = reader.GetString(9),
        Adjuster = reader.IsDBNull(10) ? null : reader.GetString(10),
        CreatedUtc = DateTime.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind)
    };

    private static LegacyClaim Clone(LegacyClaim claim) => new()
    {
        Id = claim.Id,
        ClaimReference = claim.ClaimReference,
        PolicyId = claim.PolicyId,
        PolicyholderName = claim.PolicyholderName,
        Status = claim.Status,
        ClaimedAmount = claim.ClaimedAmount,
        Deductible = claim.Deductible,
        PolicyLimit = claim.PolicyLimit,
        ReserveAmount = claim.ReserveAmount,
        Currency = claim.Currency,
        Adjuster = claim.Adjuster,
        CreatedUtc = claim.CreatedUtc
    };
}
