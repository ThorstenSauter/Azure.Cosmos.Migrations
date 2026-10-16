using Shouldly;
using ThorstenSauter.Azure.Cosmos.Migrations.Design;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tests;

public class CosmosConnectionTests
{
    private const string Key = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    [Test]
    public void Parse_BareEndpoint_UsesEntraId()
    {
        var settings = CosmosConnection.Parse("https://account.documents.azure.com:443/");

        settings.Endpoint.ShouldBe(new Uri("https://account.documents.azure.com:443/"));
        settings.AccountKey.ShouldBeNull();
        settings.IsEmulator.ShouldBeFalse();
    }

    [Test]
    public void Parse_EndpointWithoutKey_UsesEntraId()
    {
        var settings = CosmosConnection.Parse("AccountEndpoint=https://account.documents.azure.com:443/;");

        settings.AccountKey.ShouldBeNull();
        settings.IsEmulator.ShouldBeFalse();
    }

    [Test]
    public void Parse_EndpointWithKey_UsesKey()
    {
        var settings = CosmosConnection.Parse($"AccountEndpoint=https://account.documents.azure.com:443/;AccountKey={Key};");

        settings.AccountKey.ShouldBe(Key);
    }

    [Test]
    [Arguments("http://localhost:8081/")]
    [Arguments("https://127.0.0.1:8081/")]
    [Arguments("http://cosmos-emulator:8081/")]
    public void Parse_LoopbackOrHttpEndpoint_IsEmulator(string endpoint)
    {
        CosmosConnection.Parse($"AccountEndpoint={endpoint};AccountKey={Key};").IsEmulator.ShouldBeTrue();
    }

    [Test]
    public void Parse_DisableValidationOnLoopback_IsHonoured()
    {
        var settings = CosmosConnection.Parse(
            $"AccountEndpoint=https://127.0.0.1:8081/;AccountKey={Key};DisableServerCertificateValidation=True;");

        settings.DisableServerCertificateValidation.ShouldBeTrue();
    }

    [Test]
    public void Parse_DisableValidationOnRemoteHost_IsIgnored()
    {
        var settings = CosmosConnection.Parse(
            $"AccountEndpoint=https://account.documents.azure.com:443/;AccountKey={Key};DisableServerCertificateValidation=True;");

        settings.DisableServerCertificateValidation.ShouldBeFalse();
    }

    [Test]
    [Arguments("AccountKey=abc;")]
    [Arguments("AccountEndpoint=not-a-uri;")]
    [Arguments("AccountEndpoint=ftp://host/;")]
    [Arguments("=;=")]
    public void Parse_NoUsableEndpoint_Throws(string connectionString)
    {
        Should.Throw<ArgumentException>(() => CosmosConnection.Parse(connectionString));
    }
}
