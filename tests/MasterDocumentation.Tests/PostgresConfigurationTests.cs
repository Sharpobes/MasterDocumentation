using MasterDocumentation.Storage;

namespace MasterDocumentation.Tests;

/// <summary>
/// Разбор строки подключения к PostgreSQL и расшифровка ошибок. Сервер для этих проверок
/// не нужен: проверяется то, что пользователь видит до подключения.
/// </summary>
public sealed class PostgresConfigurationTests
{
    [Fact]
    public void ValidConnectionStringIsAccepted()
    {
        Assert.True(PostgresConnectionString.TryValidate("Host=localhost;Port=5432;Database=test;Username=myuser;Password=mypassword", out var error));
        Assert.Equal("", error);
    }

    [Fact]
    public void DashInsteadOfEqualsNamesTheBrokenFragment()
    {
        Assert.False(PostgresConnectionString.TryValidate("Host-localhost;Port=5432;Database=test;Username=myuser;Password=mypassword", out var error));
        Assert.Contains("Host-localhost", error);
        Assert.Contains("дефис", error);
        Assert.Contains("Host=localhost", error);
    }

    [Fact]
    public void MissingRequiredParametersAreListed()
    {
        Assert.False(PostgresConnectionString.TryValidate("Host=localhost;Port=5432", out var error));
        Assert.Contains("Database", error);
        Assert.Contains("Username", error);
    }

    [Fact]
    public void EmptyConnectionStringIsRejected()
    {
        Assert.False(PostgresConnectionString.TryValidate("   ", out var error));
        Assert.Contains("не заполнена", error);
    }

    [Fact]
    public void StoreConstructorReportsBrokenConnectionString()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new PostgresDocumentStore("Host-localhost;Database=test"));
        Assert.Contains("Host-localhost", exception.Message);
    }

    [Fact]
    public void TestConnectionRejectsBrokenConnectionStringWithoutServer()
    {
        var config = new StorageProviderConfig { Provider = StorageProviderKind.Postgres, PostgresConnectionString = "Host-localhost;Port=5432" };
        Assert.False(StorageConfigService.TestConnection(config, out var error, out var missing, out var failure));
        Assert.Contains("Host-localhost", error);
        Assert.False(missing);
        Assert.Null(failure);
    }

    [Fact]
    public void ConnectionStringErrorIsExplainedInDetail()
    {
        var details = PostgresErrorInfo.Detailed(new ArgumentException("Couldn't set host-localhost;port"));
        Assert.Contains("Строка подключения заполнена неверно", details);
        Assert.Contains("Что сделать:", details);
        Assert.Contains("Технические подробности:", details);
    }
}
