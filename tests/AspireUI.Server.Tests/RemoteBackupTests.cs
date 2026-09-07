using AspireUI.Server.Services;

// Off-site backup targets: the configuration round-trip, and the S3 signing that has to be exactly
// right or the bucket says no with a message about a signature.
public class RemoteBackupTests
{
    private static (RemoteBackupService svc, SettingsStore settings, SecretStore secrets) Fresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "aspireui-remote-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "aspireui.db");
        var settings = new SettingsStore(db);
        var secrets = new SecretStore(db, root);
        return (new RemoteBackupService(settings, secrets), settings, secrets);
    }

    [Fact]
    public void Nothing_configured_means_nothing_happens()
    {
        var (svc, _, _) = Fresh();
        Assert.False(svc.Config().Enabled);
        var (ok, error) = svc.UploadAsync("whatever", "key").GetAwaiter().GetResult();
        Assert.False(ok);
        Assert.Contains("no off-site target", error);
    }

    [Fact]
    public void An_s3_target_round_trips_and_keeps_the_secret_out_of_the_settings()
    {
        var (svc, settings, secrets) = Fresh();
        svc.Save(new RemoteBackupConfig(RemoteBackupConfig.S3, Endpoint: "https://s3.example.com",
            Region: "eu-central-1", Bucket: "backups", AccessKey: "AKIA123", SecretKey: "the-secret", PathStyle: true));

        var c = svc.Config();
        Assert.True(c.Enabled);
        Assert.Equal("backups", c.Bucket);
        Assert.Equal("the-secret", c.SecretKey);

        // The settings row holds a reference, not the secret.
        var stored = settings.GetValue("BackupS3SecretKey")!;
        Assert.NotEqual("the-secret", stored);
        Assert.Equal("the-secret", secrets.Resolve(stored));
    }

    [Fact]
    public void Saving_without_a_secret_keeps_the_one_that_is_there()
    {
        var (svc, _, _) = Fresh();
        svc.Save(new RemoteBackupConfig(RemoteBackupConfig.S3, Bucket: "backups", SecretKey: "keep-me"));
        // What the UI sends back after showing *** in the field.
        svc.Save(new RemoteBackupConfig(RemoteBackupConfig.S3, Bucket: "other", SecretKey: null));

        Assert.Equal("other", svc.Config().Bucket);
        Assert.Equal("keep-me", svc.Config().SecretKey);
    }

    [Fact]
    public void An_unknown_kind_is_stored_as_none()
    {
        var (svc, _, _) = Fresh();
        svc.Save(new RemoteBackupConfig("dropbox-maybe"));
        Assert.False(svc.Config().Enabled);
        Assert.Equal(RemoteBackupConfig.None, svc.Config().Kind);
    }

    [Fact]
    public void The_sigv4_signing_key_matches_the_documented_derivation()
    {
        // From AWS's own SigV4 example: this chain is the part that is easy to get subtly wrong.
        var key = RemoteBackupService.SigningKey("wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "20150830", "us-east-1", "iam");
        Assert.Equal("c4afb1cc5771d871763a393e44b703571b55cc28424d1a5e86da6ed3c154a4b9",
            RemoteBackupService.Hex(key));
    }

    [Fact]
    public void A_signed_request_carries_the_three_headers_a_bucket_checks()
    {
        var c = new RemoteBackupConfig(RemoteBackupConfig.S3, Endpoint: "https://s3.example.com",
            Region: "eu-central-1", Bucket: "backups", AccessKey: "AKIA123", SecretKey: "shh");
        using var req = new HttpRequestMessage(HttpMethod.Put, "https://s3.example.com/backups/app/stamp/data.tgz");

        RemoteBackupService.Sign(req, c, [1, 2, 3]);

        Assert.True(req.Headers.Contains("x-amz-date"));
        Assert.True(req.Headers.Contains("x-amz-content-sha256"));
        var auth = string.Join("", req.Headers.GetValues("Authorization"));
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIA123/", auth);
        Assert.Contains("/eu-central-1/s3/aws4_request", auth);
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date", auth);
        // The payload hash is of the body, not of nothing.
        Assert.Equal(RemoteBackupService.Hex(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })),
            string.Join("", req.Headers.GetValues("x-amz-content-sha256")));
    }

    [Fact]
    public void A_different_secret_gives_a_different_signature()
    {
        string Sig(string secret)
        {
            var c = new RemoteBackupConfig(RemoteBackupConfig.S3, Region: "eu-central-1", Bucket: "b",
                AccessKey: "AKIA", SecretKey: secret);
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://s3.example.com/b/k");
            RemoteBackupService.Sign(req, c, []);
            return string.Join("", req.Headers.GetValues("Authorization"));
        }
        Assert.NotEqual(Sig("one"), Sig("two"));
    }

    [Fact]
    public void The_ssh_user_and_the_webdav_user_are_not_the_same_user()
    {
        var (svc, _, _) = Fresh();
        svc.Save(new RemoteBackupConfig(RemoteBackupConfig.WebDav, BaseUrl: "https://cloud.example.com", User: "dav-user"));
        Assert.Equal("dav-user", svc.Config().User);

        svc.Save(new RemoteBackupConfig(RemoteBackupConfig.Sftp, Host: "nas.local", User: "deploy", Path: "/srv/backups"));
        Assert.Equal("deploy", svc.Config().User);

        // And switching back finds the other one again.
        svc.Save(new RemoteBackupConfig(RemoteBackupConfig.WebDav, BaseUrl: "https://cloud.example.com", User: "dav-user"));
        Assert.Equal("dav-user", svc.Config().User);
    }

    [Fact]
    public void An_s3_key_is_escaped_per_segment_and_the_signature_covers_that_path()
    {
        var pathStyle = new RemoteBackupConfig(RemoteBackupConfig.S3, Endpoint: "https://s3.example.com",
            Bucket: "my-backups", Region: "eu-central-1", AccessKey: "AKIA", SecretKey: "shh");

        var uri = RemoteBackupService.S3Uri(pathStyle, "stack id/2026-09-07 12:00/app data.tgz");
        // AbsolutePath, not ToString: the latter unescapes for display, the former is what is sent
        // and what the signature is computed over.
        Assert.Equal("/my-backups/stack%20id/2026-09-07%2012%3A00/app%20data.tgz", uri.AbsolutePath);
        Assert.Equal("s3.example.com", uri.Host);

        // Virtual-host style puts the bucket in the host and not in the path.
        var virtualHost = pathStyle with { PathStyle = false };
        var vhUri = RemoteBackupService.S3Uri(virtualHost, "key.tgz");
        Assert.Equal("my-backups.s3.example.com", vhUri.Host);
        Assert.Equal("/key.tgz", vhUri.AbsolutePath);

        // A listing keeps its query.
        Assert.Contains("?list-type=2", RemoteBackupService.S3Uri(pathStyle, "", "list-type=2&prefix=a").ToString());

        using var req = new HttpRequestMessage(HttpMethod.Put, uri);
        RemoteBackupService.Sign(req, pathStyle, []);
        Assert.Contains("Signature=", string.Join("", req.Headers.GetValues("Authorization")));
    }
}
