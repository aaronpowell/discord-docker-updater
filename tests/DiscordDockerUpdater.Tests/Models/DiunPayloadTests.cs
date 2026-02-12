using System.Text.Json;
using DiscordDockerUpdater.Models;

namespace DiscordDockerUpdater.Tests.Models;

public class DiunPayloadTests
{
    [Fact]
    public void DiunPayload_DeserializesFullPayload_Successfully()
    {
        // Arrange
        var json = """
        {
            "diun_version": "4.24.0",
            "hostname": "docker-host",
            "status": "update",
            "provider": "docker",
            "image": "nginx:latest",
            "hub_link": "https://hub.docker.com/r/library/nginx",
            "mime_type": "application/vnd.docker.distribution.manifest.v2+json",
            "digest": "sha256:abc123def456",
            "created": "2024-01-15T10:30:00Z",
            "platform": "linux/amd64",
            "metadata": {
                "ctn_command": "/docker-entrypoint.sh nginx -g 'daemon off;'",
                "ctn_createdat": "2024-01-15 09:00:00 +0000 UTC",
                "ctn_id": "container123abc",
                "ctn_names": "web-server",
                "ctn_size": "187MB",
                "ctn_state": "running",
                "ctn_status": "Up 2 days"
            }
        }
        """;

        // Act
        var payload = JsonSerializer.Deserialize<DiunPayload>(json);

        // Assert
        Assert.NotNull(payload);
        Assert.Equal("4.24.0", payload.DiunVersion);
        Assert.Equal("docker-host", payload.Hostname);
        Assert.Equal("update", payload.Status);
        Assert.Equal("docker", payload.Provider);
        Assert.Equal("nginx:latest", payload.Image);
        Assert.Equal("https://hub.docker.com/r/library/nginx", payload.HubLink);
        Assert.Equal("application/vnd.docker.distribution.manifest.v2+json", payload.MimeType);
        Assert.Equal("sha256:abc123def456", payload.Digest);
        Assert.Equal(DateTime.Parse("2024-01-15T10:30:00Z").ToUniversalTime(), payload.Created);
        Assert.Equal("linux/amd64", payload.Platform);

        Assert.NotNull(payload.Metadata);
        Assert.Equal("/docker-entrypoint.sh nginx -g 'daemon off;'", payload.Metadata.CtnCommand);
        Assert.Equal("2024-01-15 09:00:00 +0000 UTC", payload.Metadata.CtnCreatedAt);
        Assert.Equal("container123abc", payload.Metadata.CtnId);
        Assert.Equal("web-server", payload.Metadata.CtnNames);
        Assert.Equal("187MB", payload.Metadata.CtnSize);
        Assert.Equal("running", payload.Metadata.CtnState);
        Assert.Equal("Up 2 days", payload.Metadata.CtnStatus);
    }

    [Fact]
    public void DiunPayload_DeserializesMinimalPayload_WithMissingOptionalFields()
    {
        // Arrange
        var json = """
        {
            "image": "redis:7"
        }
        """;

        // Act
        var payload = JsonSerializer.Deserialize<DiunPayload>(json);

        // Assert
        Assert.NotNull(payload);
        Assert.Equal("redis:7", payload.Image);
        
        // All other fields should be null
        Assert.Null(payload.DiunVersion);
        Assert.Null(payload.Hostname);
        Assert.Null(payload.Status);
        Assert.Null(payload.Provider);
        Assert.Null(payload.HubLink);
        Assert.Null(payload.MimeType);
        Assert.Null(payload.Digest);
        Assert.Null(payload.Created);
        Assert.Null(payload.Platform);
        Assert.Null(payload.Metadata);
    }

    [Fact]
    public void DiunPayload_DeserializesWithPartialMetadata_Successfully()
    {
        // Arrange
        var json = """
        {
            "image": "postgres:15",
            "status": "new",
            "metadata": {
                "ctn_id": "abc123",
                "ctn_names": "db-server"
            }
        }
        """;

        // Act
        var payload = JsonSerializer.Deserialize<DiunPayload>(json);

        // Assert
        Assert.NotNull(payload);
        Assert.Equal("postgres:15", payload.Image);
        Assert.Equal("new", payload.Status);
        
        Assert.NotNull(payload.Metadata);
        Assert.Equal("abc123", payload.Metadata.CtnId);
        Assert.Equal("db-server", payload.Metadata.CtnNames);
        
        // Missing metadata fields should be null
        Assert.Null(payload.Metadata.CtnCommand);
        Assert.Null(payload.Metadata.CtnCreatedAt);
        Assert.Null(payload.Metadata.CtnSize);
        Assert.Null(payload.Metadata.CtnState);
        Assert.Null(payload.Metadata.CtnStatus);
    }

    [Fact]
    public void DiunPayload_DeserializesEmptyObject_ReturnsObjectWithNullFields()
    {
        // Arrange
        var json = "{}";

        // Act
        var payload = JsonSerializer.Deserialize<DiunPayload>(json);

        // Assert
        Assert.NotNull(payload);
        Assert.Null(payload.Image);
        Assert.Null(payload.Status);
        Assert.Null(payload.Metadata);
    }

    [Fact]
    public void DiunPayload_SerializesAndDeserializes_PreservesData()
    {
        // Arrange
        var original = new DiunPayload
        {
            DiunVersion = "4.24.0",
            Hostname = "test-host",
            Status = "update",
            Provider = "docker",
            Image = "alpine:latest",
            HubLink = "https://hub.docker.com/r/library/alpine",
            MimeType = "application/vnd.docker.distribution.manifest.v2+json",
            Digest = "sha256:xyz789",
            Created = DateTime.Parse("2024-02-01T12:00:00Z").ToUniversalTime(),
            Platform = "linux/amd64",
            Metadata = new DiunMetadata
            {
                CtnCommand = "/bin/sh",
                CtnCreatedAt = "2024-02-01 10:00:00 +0000 UTC",
                CtnId = "container789",
                CtnNames = "test-container",
                CtnSize = "5MB",
                CtnState = "running",
                CtnStatus = "Up 1 hour"
            }
        };

        // Act
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<DiunPayload>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.DiunVersion, deserialized.DiunVersion);
        Assert.Equal(original.Hostname, deserialized.Hostname);
        Assert.Equal(original.Status, deserialized.Status);
        Assert.Equal(original.Provider, deserialized.Provider);
        Assert.Equal(original.Image, deserialized.Image);
        Assert.Equal(original.HubLink, deserialized.HubLink);
        Assert.Equal(original.MimeType, deserialized.MimeType);
        Assert.Equal(original.Digest, deserialized.Digest);
        Assert.Equal(original.Created, deserialized.Created);
        Assert.Equal(original.Platform, deserialized.Platform);
        
        Assert.NotNull(deserialized.Metadata);
        Assert.Equal(original.Metadata.CtnCommand, deserialized.Metadata.CtnCommand);
        Assert.Equal(original.Metadata.CtnCreatedAt, deserialized.Metadata.CtnCreatedAt);
        Assert.Equal(original.Metadata.CtnId, deserialized.Metadata.CtnId);
        Assert.Equal(original.Metadata.CtnNames, deserialized.Metadata.CtnNames);
        Assert.Equal(original.Metadata.CtnSize, deserialized.Metadata.CtnSize);
        Assert.Equal(original.Metadata.CtnState, deserialized.Metadata.CtnState);
        Assert.Equal(original.Metadata.CtnStatus, deserialized.Metadata.CtnStatus);
    }

    [Fact]
    public void DiunPayload_DeserializesWithNullMetadata_Successfully()
    {
        // Arrange
        var json = """
        {
            "image": "ubuntu:22.04",
            "status": "update",
            "metadata": null
        }
        """;

        // Act
        var payload = JsonSerializer.Deserialize<DiunPayload>(json);

        // Assert
        Assert.NotNull(payload);
        Assert.Equal("ubuntu:22.04", payload.Image);
        Assert.Equal("update", payload.Status);
        Assert.Null(payload.Metadata);
    }

    [Fact]
    public void DiunPayload_HandlesInvalidJson_ThrowsJsonException()
    {
        // Arrange
        var invalidJson = "{ invalid json }";

        // Act & Assert
        Assert.Throws<JsonException>(() => 
            JsonSerializer.Deserialize<DiunPayload>(invalidJson));
    }

    [Fact]
    public void DiunPayload_DeserializesWithExtraFields_IgnoresUnknownProperties()
    {
        // Arrange
        var json = """
        {
            "image": "nginx:alpine",
            "status": "new",
            "unknown_field": "this should be ignored",
            "another_unknown": 12345
        }
        """;

        // Act
        var payload = JsonSerializer.Deserialize<DiunPayload>(json);

        // Assert
        Assert.NotNull(payload);
        Assert.Equal("nginx:alpine", payload.Image);
        Assert.Equal("new", payload.Status);
        // Unknown fields are ignored, no exception thrown
    }

    [Fact]
    public void DiunMetadata_DeserializesEmpty_ReturnsObjectWithNullFields()
    {
        // Arrange
        var json = "{}";

        // Act
        var metadata = JsonSerializer.Deserialize<DiunMetadata>(json);

        // Assert
        Assert.NotNull(metadata);
        Assert.Null(metadata.CtnCommand);
        Assert.Null(metadata.CtnCreatedAt);
        Assert.Null(metadata.CtnId);
        Assert.Null(metadata.CtnNames);
        Assert.Null(metadata.CtnSize);
        Assert.Null(metadata.CtnState);
        Assert.Null(metadata.CtnStatus);
    }
}
