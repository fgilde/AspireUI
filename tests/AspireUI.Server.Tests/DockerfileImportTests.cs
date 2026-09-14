using AspireUI.Server.Models;
using AspireUI.Server.Services;

// A repository with a Dockerfile and nothing else — no compose file, no AppHost, no manifest — is a
// perfectly good app: the Dockerfile says which port, which volumes and which settings, and the
// image is either published already or built on the spot.
public class DockerfileImportTests
{
    private const string MeTube = """
        FROM node:22-alpine AS builder
        WORKDIR /metube
        ENV NODE_ENV=production
        EXPOSE 4200
        RUN npm ci

        FROM python:3.13-slim
        WORKDIR /app
        ARG TARGETARCH
        ENV PUID=1000
        ENV PGID=1000
        ENV UMASK=022
        ENV DOWNLOAD_DIR=/downloads \
            STATE_DIR=/downloads/.metube
        ENV PORT=8081
        ARG VERSION=dev
        ENV METUBE_VERSION=$VERSION
        # the app keeps everything under one directory
        VOLUME /downloads
        EXPOSE 8081/tcp
        ENTRYPOINT ["/usr/bin/tini", "-g", "--", "./docker-entrypoint.sh"]
        """;

    [Fact]
    public void Only_the_last_stage_describes_the_image()
    {
        var info = DockerfileReader.Parse(MeTube);
        Assert.Equal(8081, info.Port);
        Assert.Equal(["/downloads"], info.Volumes);
        Assert.Equal(["PUID", "PGID", "UMASK", "DOWNLOAD_DIR", "STATE_DIR", "PORT"], info.Env.Select(e => e.Key));
        Assert.Equal("/downloads/.metube", info.Env.Single(e => e.Key == "STATE_DIR").Value);
    }

    [Fact]
    public void The_older_forms_are_read_too()
    {
        var info = DockerfileReader.Parse("FROM alpine\nENV APP_HOME /srv/app\nVOLUME [\"/data\", \"/config\"]\nEXPOSE 80 443\n");
        Assert.Equal(80, info.Port);
        Assert.Equal(["/data", "/config"], info.Volumes);
        Assert.Equal("/srv/app", info.Env.Single().Value);
    }

    [Fact]
    public void A_dockerfile_without_expose_has_no_port()
    {
        Assert.Null(DockerfileReader.Parse("FROM alpine\nCMD [\"sh\"]\n").Port);
    }

    private static string Repo(string dockerfile)
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-dfimport-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Dockerfile"), dockerfile);
        File.WriteAllText(Path.Combine(dir, "README.md"), "# fork");
        return dir;
    }

    private static DirImporter Importer() => new(new ImportService(), new ComposeImporter());

    [Fact]
    public void A_repository_with_only_a_dockerfile_asks_for_the_dockerfile_mode()
    {
        var dir = Repo(MeTube);
        try { Assert.Equal("dockerfile", DirImporter.ModeFor(dir)); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_compose_file_still_wins_over_the_dockerfile()
    {
        var dir = Repo(MeTube);
        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), "services:\n  app:\n    build: .\n");
        try { Assert.Equal("compose", DirImporter.ModeFor(dir)); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void With_a_published_image_the_stack_pulls_it()
    {
        var dir = Repo(MeTube);
        try
        {
            var (stack, error) = Importer().Build("s1", dir, "dockerfile", "MeTube fork", null, null,
                new() { ["PUID"] = "1000", ["UMASK"] = "022" }, image: "ghcr.io/fgilde/metube:latest");
            Assert.Null(error);
            var node = Assert.Single(stack!.Nodes);
            Assert.Equal("AddContainer", node.AddMethod);
            Assert.Equal(["\"ghcr.io/fgilde/metube:latest\""], node.AddArgs);
            Assert.Contains(node.WithCalls, w => w.Method == "WithHttpEndpoint" && w.Args.Contains("targetPort: 8081"));
            Assert.Contains(node.WithCalls, w => w.Method == "WithVolume" && w.Args[1] == "\"/downloads\"");
            Assert.Equal(2, node.WithCalls.Count(w => w.Method == "WithEnvironment"));
            Assert.True(stack.HasSource);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Without_an_image_the_stack_builds_the_dockerfile()
    {
        var dir = Repo(MeTube);
        try
        {
            var (stack, error) = Importer().Build("s1", dir, "dockerfile", "metube", null, null, null);
            Assert.Null(error);
            var node = Assert.Single(stack!.Nodes);
            Assert.Equal("AddDockerfile", node.AddMethod);
            Assert.Equal(["\".\""], node.AddArgs);
            Assert.DoesNotContain(node.WithCalls, w => w.Method == "WithEnvironment");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void No_expose_means_the_port_has_to_be_given()
    {
        var dir = Repo("FROM alpine\nCMD [\"sh\"]\n");
        try
        {
            var (none, error) = Importer().Build("s1", dir, "dockerfile", "thing", null, null, null);
            Assert.Null(none);
            Assert.Contains("port", error, StringComparison.OrdinalIgnoreCase);

            var (stack, _) = Importer().Build("s1", dir, "dockerfile", "thing", null, null, null, port: 3000);
            Assert.Contains(stack!.Nodes.Single().WithCalls, w => w.Method == "WithHttpEndpoint" && w.Args.Contains("targetPort: 3000"));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ImageRegistryTests
{
    [Theory]
    [InlineData("ghcr.io/fgilde/metube:latest", "ghcr.io", "fgilde/metube", "latest")]
    [InlineData("ghcr.io/fgilde/metube", "ghcr.io", "fgilde/metube", "latest")]
    [InlineData("postgres:16", "registry-1.docker.io", "library/postgres", "16")]
    [InlineData("docker.io/alexta69/metube:2024", "registry-1.docker.io", "alexta69/metube", "2024")]
    [InlineData("localhost:5000/app@sha256:abcd", "localhost:5000", "app", "sha256:abcd")]
    public void An_image_reference_splits_the_way_a_pull_reads_it(string image, string registry, string repo, string reference)
    {
        var r = ImageRegistry.Parse(image);
        Assert.Equal(registry, r.Registry);
        Assert.Equal(repo, r.Repository);
        Assert.Equal(reference, r.Reference);
    }

    [Theory]
    [InlineData("https://github.com/fgilde/metube", "ghcr.io/fgilde/metube:latest")]
    [InlineData("https://github.com/Fgilde/MeTube.git", "ghcr.io/fgilde/metube:latest")]
    [InlineData("git@github.com:fgilde/metube.git", "ghcr.io/fgilde/metube:latest")]
    [InlineData("https://gitlab.com/fgilde/metube", null)]
    [InlineData("https://github.com/fgilde", null)]
    public void Github_repositories_suggest_their_ghcr_image(string url, string? expected)
    {
        Assert.Equal(expected, ImageRegistry.SuggestFor(url));
    }
}
