using PgDataHub.Config;

namespace PgDataHub.Tests;

public sealed class ConfigPersistenceTests
{
    [Fact]
    public void Volatile_opt_in_and_separate_permissions_survive_save_and_reload()
    {
        using var file = TempIni.Create("[function]\ninclude=*\n");
        var config = PgDataHubConfig.Load(file.Path);
        Assert.Empty(config.Function.VolatileInclude);
        config.Function.VolatileInclude = ["public.write_*"];
        config.Roles = [new() { Name = "writer", FunctionsAllow = ["public.*"],
            FunctionsVolatileAllow = ["public.write_*"], FunctionsVolatileDeny = ["public.write_secret"] }];
        IniAuthWriter.SaveAll(config);
        var reloaded = PgDataHubConfig.Load(file.Path);
        Assert.Equal(config.Function.VolatileInclude, reloaded.Function.VolatileInclude);
        Assert.Equal(config.Roles[0].FunctionsVolatileAllow, reloaded.Roles[0].FunctionsVolatileAllow);
        Assert.Equal(config.Roles[0].FunctionsVolatileDeny, reloaded.Roles[0].FunctionsVolatileDeny);
    }

    [Fact]
    public void SaveAll_preserves_unknown_sections_and_removes_temporary_file()
    {
        using var file = TempIni.Create("# keep me\n[custom.plugin]\nvalue=untouched\n\n[feature]\ndefaultLimit=1\nmaxLimit=2\n");
        var config = new PgDataHubConfig { SourcePath = file.Path };
        config.Database.Database = "db";
        config.Database.User = "user";
        config.Feature.DefaultLimit = 3;
        config.Feature.MaxLimit = 4;

        IniAuthWriter.SaveAll(config);

        var content = File.ReadAllText(file.Path).Replace("\r\n", "\n");
        Assert.Contains("[custom.plugin]\nvalue=untouched", content, StringComparison.Ordinal);
        Assert.Contains("defaultLimit=3", content, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(file.Path)!, $".{Path.GetFileName(file.Path)}.*.tmp"));
    }

    [Fact]
    public void Save_persists_hash_but_never_raw_api_key()
    {
        using var file = TempIni.Create("[database]\ndatabase=db\nuser=user\n");
        const string rawKey = "raw-secret-that-must-not-be-written";
        var config = PgDataHubConfig.Load(file.Path);
        config.ApiKeys.Add(new AuthApiKeyConfig
        {
            Id = 1, Name = "test", Role = "admin", Enabled = true,
            Hash = PgDataHub.Auth.AuthService.HashKey(rawKey)
        });

        IniAuthWriter.Save(config);

        var content = File.ReadAllText(file.Path);
        Assert.DoesNotContain(rawKey, content, StringComparison.Ordinal);
        Assert.Contains(config.ApiKeys[0].Hash, content, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_parses_collection_function_and_auth_globs()
    {
        using var file = TempIni.Create("""
            [database]
            database=db
            user=user
            [collections]
            include=public.*, maps.roads
            exclude=public.secret
            [function]
            include=public.get_*
            exclude=public.get_secret
            [auth]
            enabled=true
            adminRole=admin
            """);

        var config = PgDataHubConfig.Load(file.Path);

        Assert.Equal(["public.*", "maps.roads"], config.Collections.Include);
        Assert.Equal(["public.secret"], config.Collections.Exclude);
        Assert.Equal(["public.get_*"], config.Function.Include);
        Assert.Equal(["public.get_secret"], config.Function.Exclude);
        Assert.True(config.Auth.Enabled);
        Assert.Equal("admin", config.Auth.AdminRole);
    }

    private sealed class TempIni : IDisposable
    {
        private TempIni(string path) => Path = path;
        internal string Path { get; }

        internal static TempIni Create(string content)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pgdatahub-test-{Guid.NewGuid():N}.ini");
            File.WriteAllText(path, content);
            return new TempIni(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
