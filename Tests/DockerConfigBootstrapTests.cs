using PgDataHub.Config;

namespace PgDataHub.Tests;

public sealed class DockerConfigBootstrapTests
{
    [Fact]
    public void Apply_is_idempotent_and_rotates_only_the_docker_admin_key()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgdatahub-docker-{Guid.NewGuid():N}.ini");
        var previous = Environment.GetEnvironmentVariable("PGDATAHUB_ADMIN_KEY");
        try
        {
            File.WriteAllText(path, "[database]\ndatabase=db\nuser=user\n");
            var config = PgDataHubConfig.Load(path);
            config.ApiKeys.Add(new AuthApiKeyConfig
            {
                Id = 99, Name = "manual", Owner = "operator", Role = "admin", Enabled = true, Hash = "manual-hash"
            });

            Environment.SetEnvironmentVariable("PGDATAHUB_ADMIN_KEY", "first-docker-admin-key");
            DockerConfigBootstrap.Apply(config);
            DockerConfigBootstrap.Apply(config);
            Assert.Single(config.ApiKeys, k => k.Owner == "docker-compose");

            var firstHash = config.ApiKeys.Single(k => k.Owner == "docker-compose").Hash;
            Environment.SetEnvironmentVariable("PGDATAHUB_ADMIN_KEY", "second-docker-admin-key");
            DockerConfigBootstrap.Apply(config);

            var dockerKey = Assert.Single(config.ApiKeys, k => k.Owner == "docker-compose");
            Assert.NotEqual(firstHash, dockerKey.Hash);
            Assert.Contains(config.ApiKeys, k => k.Owner == "operator" && k.Hash == "manual-hash");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PGDATAHUB_ADMIN_KEY", previous);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Apply_rejects_short_admin_key()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgdatahub-docker-{Guid.NewGuid():N}.ini");
        var previous = Environment.GetEnvironmentVariable("PGDATAHUB_ADMIN_KEY");
        try
        {
            File.WriteAllText(path, "[database]\ndatabase=db\nuser=user\n");
            Environment.SetEnvironmentVariable("PGDATAHUB_ADMIN_KEY", "short");
            var config = PgDataHubConfig.Load(path);

            Assert.Throws<InvalidOperationException>(() => DockerConfigBootstrap.Apply(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PGDATAHUB_ADMIN_KEY", previous);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
