using System.Globalization;
using H2HClientWeb.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace H2HClientWeb.Data;

public sealed class AppRepository
{
    private readonly string _connectionString;
    private readonly IDataProtector _protector;

    public AppRepository(IWebHostEnvironment environment, IConfiguration configuration, IDataProtectionProvider protectionProvider)
    {
        var configuredPath = configuration["Data:DatabasePath"];
        var databasePath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "h2hclient.db")
            : Path.GetFullPath(configuredPath, environment.ContentRootPath);

        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        _protector = protectionProvider.CreateProtector("H2HClientWeb.MerchantCredentials.v1");
        Initialize();
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Merchants (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                TestApiKey TEXT NOT NULL DEFAULT '',
                LiveApiKey TEXT NOT NULL DEFAULT '',
                Secret TEXT NOT NULL DEFAULT '',
                BaseUrl TEXT NOT NULL,
                IsProd INTEGER NOT NULL DEFAULT 0,
                UNIQUE(Name, IsProd)
            );
            CREATE TABLE IF NOT EXISTS History (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MerchantId INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                Type TEXT NOT NULL,
                SessionId TEXT NOT NULL DEFAULT '',
                MerchantOrderId TEXT NOT NULL DEFAULT '',
                Amount TEXT NOT NULL DEFAULT '0',
                Currency TEXT NOT NULL DEFAULT '',
                Status TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS IX_History_MerchantId_CreatedAt ON History(MerchantId, CreatedAt DESC);
            CREATE TABLE IF NOT EXISTS Webhooks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ReceivedAt TEXT NOT NULL,
                Method TEXT NOT NULL,
                Path TEXT NOT NULL,
                Body TEXT NOT NULL,
                SignatureValid INTEGER NULL
            );
            CREATE TABLE IF NOT EXISTS PlatformCredentials (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                IsProd INTEGER NOT NULL,
                BaseUrl TEXT NOT NULL,
                Login TEXT NOT NULL,
                Password TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS TopUpRequests (
                Id TEXT PRIMARY KEY,
                MerchantId INTEGER NOT NULL,
                IsProd INTEGER NOT NULL,
                PlatformLogin TEXT NOT NULL,
                PlatformUserId TEXT NOT NULL,
                Amount TEXT NOT NULL,
                Status TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_TopUpRequests_MerchantId_CreatedAt ON TopUpRequests(MerchantId, CreatedAt DESC);
            """;
        command.ExecuteNonQuery();
        EnsureMerchantEnvironmentColumn(connection);
        EnsureMerchantEnvironmentUniqueIndex(connection);
        EnsureMultiplePlatformCredentials(connection);
    }

    private static void EnsureMerchantEnvironmentColumn(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(Merchants);";
        using var reader = check.ExecuteReader();
        var hasColumn = false;
        while (reader.Read())
            if (string.Equals(reader.GetString(1), "IsProd", StringComparison.OrdinalIgnoreCase)) hasColumn = true;
        reader.Close();
        if (hasColumn) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Merchants ADD COLUMN IsProd INTEGER NOT NULL DEFAULT 0;";
        alter.ExecuteNonQuery();
    }

    private static void EnsureMerchantEnvironmentUniqueIndex(SqliteConnection connection)
    {
        using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='Merchants';";
        var tableSql = Convert.ToString(schema.ExecuteScalar()) ?? "";
        if (!tableSql.Contains("Name TEXT NOT NULL UNIQUE", StringComparison.OrdinalIgnoreCase)) return;

        using var transaction = connection.BeginTransaction();
        using var rebuild = connection.CreateCommand();
        rebuild.Transaction = transaction;
        rebuild.CommandText = """
            CREATE TABLE Merchants_New (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                TestApiKey TEXT NOT NULL DEFAULT '',
                LiveApiKey TEXT NOT NULL DEFAULT '',
                Secret TEXT NOT NULL DEFAULT '',
                BaseUrl TEXT NOT NULL,
                IsProd INTEGER NOT NULL DEFAULT 0,
                UNIQUE(Name, IsProd)
            );
            INSERT INTO Merchants_New(Id, Name, TestApiKey, LiveApiKey, Secret, BaseUrl, IsProd)
            SELECT Id, Name, TestApiKey, LiveApiKey, Secret, BaseUrl, IsProd FROM Merchants;
            DROP TABLE Merchants;
            ALTER TABLE Merchants_New RENAME TO Merchants;
            """;
        rebuild.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<Merchant> GetMerchants()
    {
        var merchants = new List<Merchant>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, TestApiKey, LiveApiKey, Secret, BaseUrl, IsProd FROM Merchants ORDER BY Name;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) merchants.Add(ReadMerchant(reader));
        return merchants;
    }

    public IReadOnlyList<Merchant> GetMerchants(bool isProd) =>
        GetMerchants().Where(merchant => merchant.IsProd == isProd).ToArray();

    public Merchant? GetMerchant(int id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, TestApiKey, LiveApiKey, Secret, BaseUrl, IsProd FROM Merchants WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMerchant(reader) : null;
    }

    public int SaveMerchant(Merchant merchant)
    {
        var existing = merchant.Id > 0 ? GetMerchant(merchant.Id) : null;
        if (existing is not null)
        {
            if (string.IsNullOrWhiteSpace(merchant.TestApiKey)) merchant.TestApiKey = existing.TestApiKey;
            if (string.IsNullOrWhiteSpace(merchant.LiveApiKey)) merchant.LiveApiKey = existing.LiveApiKey;
            if (string.IsNullOrWhiteSpace(merchant.Secret)) merchant.Secret = existing.Secret;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (merchant.Id > 0)
        {
            command.CommandText = """
                UPDATE Merchants SET Name=$name, TestApiKey=$test, LiveApiKey=$live, Secret=$secret, BaseUrl=$url, IsProd=$isProd
                WHERE Id=$id;
                """;
            command.Parameters.AddWithValue("$id", merchant.Id);
        }
        else
        {
            command.CommandText = """
                INSERT INTO Merchants(Name, TestApiKey, LiveApiKey, Secret, BaseUrl, IsProd)
                VALUES($name, $test, $live, $secret, $url, $isProd);
                SELECT last_insert_rowid();
                """;
        }

        command.Parameters.AddWithValue("$name", merchant.Name.Trim());
        command.Parameters.AddWithValue("$test", Protect(merchant.TestApiKey));
        command.Parameters.AddWithValue("$live", Protect(merchant.LiveApiKey));
        command.Parameters.AddWithValue("$secret", Protect(merchant.Secret));
        command.Parameters.AddWithValue("$url", merchant.BaseUrl.TrimEnd('/'));
        command.Parameters.AddWithValue("$isProd", merchant.IsProd ? 1 : 0);

        if (merchant.Id > 0)
        {
            command.ExecuteNonQuery();
            return merchant.Id;
        }

        return Convert.ToInt32((long)(command.ExecuteScalar() ?? 0L));
    }

    private static void EnsureMultiplePlatformCredentials(SqliteConnection connection)
    {
        using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='PlatformCredentials';";
        var tableSql = Convert.ToString(schema.ExecuteScalar()) ?? "";
        if (tableSql.Contains("Id INTEGER PRIMARY KEY AUTOINCREMENT", StringComparison.OrdinalIgnoreCase)) return;

        using var transaction = connection.BeginTransaction();
        using var rebuild = connection.CreateCommand();
        rebuild.Transaction = transaction;
        rebuild.CommandText = """
            CREATE TABLE PlatformCredentials_New (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                IsProd INTEGER NOT NULL,
                BaseUrl TEXT NOT NULL,
                Login TEXT NOT NULL,
                Password TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            INSERT INTO PlatformCredentials_New(IsProd, BaseUrl, Login, Password, UpdatedAt)
            SELECT IsProd, BaseUrl, Login, Password, CURRENT_TIMESTAMP FROM PlatformCredentials;
            DROP TABLE PlatformCredentials;
            ALTER TABLE PlatformCredentials_New RENAME TO PlatformCredentials;
            """;
        rebuild.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<PlatformCredential> GetPlatformCredentials(bool isProd)
    {
        var result = new List<PlatformCredential>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, IsProd, BaseUrl, Login, Password FROM PlatformCredentials WHERE IsProd=$isProd ORDER BY UpdatedAt DESC, Id DESC;";
        command.Parameters.AddWithValue("$isProd", isProd ? 1 : 0);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(ReadPlatformCredential(reader));
        return result;
    }

    public PlatformCredential? GetPlatformCredential(bool isProd, string? login = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, IsProd, BaseUrl, Login, Password FROM PlatformCredentials WHERE IsProd=$isProd ORDER BY UpdatedAt DESC, Id DESC;";
        command.Parameters.AddWithValue("$isProd", isProd ? 1 : 0);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var credential = ReadPlatformCredential(reader);
            if (string.IsNullOrWhiteSpace(login) || string.Equals(credential.Login, login.Trim(), StringComparison.OrdinalIgnoreCase))
                return credential;
        }
        return null;
    }

    public void SavePlatformCredential(PlatformCredential credential)
    {
        var existing = GetPlatformCredential(credential.IsProd, credential.Login);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = existing is null
            ? "INSERT INTO PlatformCredentials(IsProd, BaseUrl, Login, Password, UpdatedAt) VALUES($isProd, $baseUrl, $login, $password, $updatedAt);"
            : "UPDATE PlatformCredentials SET BaseUrl=$baseUrl, Login=$login, Password=$password, UpdatedAt=$updatedAt WHERE Id=$id;";
        if (existing is not null) command.Parameters.AddWithValue("$id", existing.Id);
        command.Parameters.AddWithValue("$isProd", credential.IsProd ? 1 : 0);
        command.Parameters.AddWithValue("$baseUrl", credential.BaseUrl.TrimEnd('/'));
        command.Parameters.AddWithValue("$login", Protect(credential.Login));
        command.Parameters.AddWithValue("$password", Protect(credential.Password));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeletePlatformCredential(bool isProd, string login)
    {
        var credential = GetPlatformCredentials(isProd)
            .FirstOrDefault(item => string.Equals(item.Login, login.Trim(), StringComparison.OrdinalIgnoreCase));
        if (credential is null) return;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PlatformCredentials WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", credential.Id);
        command.ExecuteNonQuery();
    }

    private PlatformCredential ReadPlatformCredential(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        IsProd = reader.GetInt32(1) == 1,
        BaseUrl = reader.GetString(2),
        Login = Unprotect(reader.GetString(3)),
        Password = Unprotect(reader.GetString(4))
    };

    public void DeleteMerchant(int id)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var history = connection.CreateCommand();
        history.Transaction = transaction;
        history.CommandText = "DELETE FROM History WHERE MerchantId=$id;";
        history.Parameters.AddWithValue("$id", id);
        history.ExecuteNonQuery();
        using var topUps = connection.CreateCommand();
        topUps.Transaction = transaction;
        topUps.CommandText = "DELETE FROM TopUpRequests WHERE MerchantId=$id;";
        topUps.Parameters.AddWithValue("$id", id);
        topUps.ExecuteNonQuery();
        using var merchant = connection.CreateCommand();
        merchant.Transaction = transaction;
        merchant.CommandText = "DELETE FROM Merchants WHERE Id=$id;";
        merchant.Parameters.AddWithValue("$id", id);
        merchant.ExecuteNonQuery();
        transaction.Commit();
    }

    public void AddHistory(HistoryRecord item)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO History(MerchantId, CreatedAt, Type, SessionId, MerchantOrderId, Amount, Currency, Status)
            VALUES($merchantId, $createdAt, $type, $sessionId, $orderId, $amount, $currency, $status);
            """;
        command.Parameters.AddWithValue("$merchantId", item.MerchantId);
        command.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$type", item.Type);
        command.Parameters.AddWithValue("$sessionId", item.SessionId);
        command.Parameters.AddWithValue("$orderId", item.MerchantOrderId);
        command.Parameters.AddWithValue("$amount", item.Amount.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$currency", item.Currency);
        command.Parameters.AddWithValue("$status", item.Status);
        command.ExecuteNonQuery();
    }

    public void AddTopUpRequest(TopUpRequest item)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TopUpRequests(Id, MerchantId, IsProd, PlatformLogin, PlatformUserId, Amount, Status, CreatedAt)
            VALUES($id, $merchantId, $isProd, $login, $userId, $amount, $status, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$merchantId", item.MerchantId);
        command.Parameters.AddWithValue("$isProd", item.IsProd ? 1 : 0);
        command.Parameters.AddWithValue("$login", item.PlatformLogin);
        command.Parameters.AddWithValue("$userId", item.PlatformUserId.ToString());
        command.Parameters.AddWithValue("$amount", item.Amount.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$status", item.Status);
        command.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<TopUpRequest> GetTopUpRequests(int merchantId, int limit = 20)
    {
        var result = new List<TopUpRequest>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, MerchantId, IsProd, PlatformLogin, PlatformUserId, Amount, Status, CreatedAt
            FROM TopUpRequests WHERE MerchantId=$merchantId ORDER BY CreatedAt DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$merchantId", merchantId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new TopUpRequest
        {
            Id = reader.GetString(0),
            MerchantId = reader.GetInt32(1),
            IsProd = reader.GetInt32(2) == 1,
            PlatformLogin = reader.GetString(3),
            PlatformUserId = Guid.TryParse(reader.GetString(4), out var userId) ? userId : Guid.Empty,
            Amount = decimal.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            Status = reader.GetString(6),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture)
        });
        return result;
    }

    public IReadOnlyList<HistoryRecord> GetHistory(int merchantId, int limit = 100)
    {
        var result = new List<HistoryRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, MerchantId, CreatedAt, Type, SessionId, MerchantOrderId, Amount, Currency, Status
            FROM History WHERE MerchantId=$merchantId ORDER BY Id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$merchantId", merchantId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new HistoryRecord
            {
                Id = reader.GetInt64(0),
                MerchantId = reader.GetInt32(1),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                Type = reader.GetString(3),
                SessionId = reader.GetString(4),
                MerchantOrderId = reader.GetString(5),
                Amount = decimal.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                Currency = reader.GetString(7),
                Status = reader.GetString(8)
            });
        }
        return result;
    }

    public void ClearHistory(int merchantId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM History WHERE MerchantId=$id;";
        command.Parameters.AddWithValue("$id", merchantId);
        command.ExecuteNonQuery();
    }

    public void AddWebhook(WebhookRecord item)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Webhooks(ReceivedAt, Method, Path, Body, SignatureValid)
            VALUES($receivedAt, $method, $path, $body, $signatureValid);
            """;
        command.Parameters.AddWithValue("$receivedAt", item.ReceivedAt.ToString("O"));
        command.Parameters.AddWithValue("$method", item.Method);
        command.Parameters.AddWithValue("$path", item.Path);
        command.Parameters.AddWithValue("$body", item.Body);
        command.Parameters.AddWithValue("$signatureValid", item.SignatureValid is null ? DBNull.Value : item.SignatureValid.Value ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<WebhookRecord> GetWebhooks(int limit = 50)
    {
        var result = new List<WebhookRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ReceivedAt, Method, Path, Body, SignatureValid FROM Webhooks ORDER BY Id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new WebhookRecord
            {
                Id = reader.GetInt64(0),
                ReceivedAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                Method = reader.GetString(2),
                Path = reader.GetString(3),
                Body = reader.GetString(4),
                SignatureValid = reader.IsDBNull(5) ? null : reader.GetInt32(5) == 1
            });
        }
        return result;
    }

    public void ClearWebhooks()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Webhooks;";
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private Merchant ReadMerchant(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Name = reader.GetString(1),
        TestApiKey = Unprotect(reader.GetString(2)),
        LiveApiKey = Unprotect(reader.GetString(3)),
        Secret = Unprotect(reader.GetString(4)),
        BaseUrl = reader.GetString(5),
        IsProd = reader.GetInt32(6) == 1
    };

    private string Protect(string value) => string.IsNullOrEmpty(value) ? "" : _protector.Protect(value);

    private string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return _protector.Unprotect(value); }
        catch { return value; }
    }
}
