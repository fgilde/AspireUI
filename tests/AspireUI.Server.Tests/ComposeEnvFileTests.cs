using AspireUI.Server.Services;

public class ComposeEnvFileTests
{
    [Fact]
    public void EnvFileKeysComeFromTheExampleWithImportValuesWinningAndEnvironmentBeatingBoth()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env.example"), """
                # comment
                DATABASE_URL=postgres://localhost/x
                AUTH_SECRET=change-me
                APP_URL="http://localhost:3000"
                SMTP_HOST=
                """);
            var (stack, error) = new ComposeImporter().Import("s1", "demo", """
                services:
                  app:
                    image: app:1
                    env_file: .env
                    environment:
                      DATABASE_URL: postgres://db/x
                """, srcDir: dir, env: new Dictionary<string, string> { ["AUTH_SECRET"] = "s3cret" });

            Assert.Null(error);
            var env = stack!.Nodes.Single().WithCalls.Where(w => w.Method == "WithEnvironment")
                .ToDictionary(w => w.Args[0].Trim('"'), w => w.Args[1].Trim('"'));
            Assert.Equal("postgres://db/x", env["DATABASE_URL"]);
            Assert.Equal("s3cret", env["AUTH_SECRET"]);
            Assert.Equal("http://localhost:3000", env["APP_URL"]);
            Assert.False(env.ContainsKey("SMTP_HOST"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
