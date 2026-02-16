using DiscordDockerUpdater.Services;

namespace DiscordDockerUpdater.Tests.Services;

public class ComposeFileUpdaterTests
{
    [Fact]
    public void UpdateImageInYaml_UpdatesCorrectService()
    {
        var yaml = """
            services:
              myapp:
                image: nginx:latest
                ports:
                  - "80:80"
              other:
                image: redis:7
            """;

        var result = ComposeFileUpdater.UpdateImageInYaml(yaml, "myapp", "nginx@sha256:abc123");

        Assert.Contains("image: nginx@sha256:abc123", result);
        Assert.Contains("image: redis:7", result);
    }

    [Fact]
    public void UpdateImageInYaml_PreservesDoubleQuotes()
    {
        var yaml = """
            services:
              myapp:
                image: "nginx:latest"
            """;

        var result = ComposeFileUpdater.UpdateImageInYaml(yaml, "myapp", "nginx@sha256:abc123");

        Assert.Contains("image: \"nginx@sha256:abc123\"", result);
    }

    [Fact]
    public void UpdateImageInYaml_PreservesSingleQuotes()
    {
        var yaml = """
            services:
              myapp:
                image: 'nginx:latest'
            """;

        var result = ComposeFileUpdater.UpdateImageInYaml(yaml, "myapp", "nginx@sha256:abc123");

        Assert.Contains("image: 'nginx@sha256:abc123'", result);
    }

    [Fact]
    public void UpdateImageInYaml_PreservesIndentation()
    {
        var yaml = "services:\n  myapp:\n    image: nginx:latest\n    ports:\n      - \"80:80\"\n";

        var result = ComposeFileUpdater.UpdateImageInYaml(yaml, "myapp", "nginx@sha256:abc123");

        Assert.Contains("    image: nginx@sha256:abc123", result);
    }

    [Fact]
    public void UpdateImageInYaml_DoesNotModifyOtherServices()
    {
        var yaml = "services:\n  app:\n    image: app:v1\n  db:\n    image: postgres:15\n";

        var result = ComposeFileUpdater.UpdateImageInYaml(yaml, "db", "postgres@sha256:def456");

        Assert.Contains("image: app:v1", result);
        Assert.Contains("image: postgres@sha256:def456", result);
    }

    [Fact]
    public void UpdateImageInYaml_NoMatch_ReturnsUnchanged()
    {
        var yaml = "services:\n  myapp:\n    image: nginx:latest\n";

        var result = ComposeFileUpdater.UpdateImageInYaml(yaml, "nonexistent", "nginx@sha256:abc123");

        Assert.Equal(yaml, result);
    }

    [Fact]
    public void UpdateImageInYaml_DoesNotMatchServiceOutsideServicesBlock()
    {
        var yaml = "volumes:\n  myapp:\n    image: should_not_change\nservices:\n  myapp:\n    image: nginx:latest\n";

        var result = ComposeFileUpdater.UpdateImageInYaml(yaml, "myapp", "nginx@sha256:abc123");

        Assert.Contains("image: should_not_change", result);
        Assert.Contains("image: nginx@sha256:abc123", result);
    }

    [Fact]
    public void BuildImageWithDigest_StripsTag()
    {
        var result = ComposeFileUpdater.BuildImageWithDigest("ghcr.io/org/repo:nightly", "sha256:abc123");

        Assert.Equal("ghcr.io/org/repo@sha256:abc123", result);
    }

    [Fact]
    public void BuildImageWithDigest_StripsExistingDigest()
    {
        var result = ComposeFileUpdater.BuildImageWithDigest("ghcr.io/org/repo@sha256:old", "sha256:new123");

        Assert.Equal("ghcr.io/org/repo@sha256:new123", result);
    }

    [Fact]
    public void BuildImageWithDigest_HandlesNoTag()
    {
        var result = ComposeFileUpdater.BuildImageWithDigest("nginx", "sha256:abc123");

        Assert.Equal("nginx@sha256:abc123", result);
    }

    [Fact]
    public void BuildImageWithDigest_HandlesRegistryWithPort()
    {
        var result = ComposeFileUpdater.BuildImageWithDigest("registry:5000/myimage:v1", "sha256:abc123");

        Assert.Equal("registry:5000/myimage@sha256:abc123", result);
    }
}
