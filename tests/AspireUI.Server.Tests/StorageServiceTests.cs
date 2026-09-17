using AspireUI.Server.Services;

// What may be deleted is the whole point of this feature, so the decision is made over docker's own
// output and tested without a docker: a stopped app that loses its image and its data has not been
// stopped, it has been deleted.
public class StorageServiceTests
{
    // One hosted app (project aspireui-abc12345) that is currently stopped, one unrelated running
    // container, and the leftovers of neither.
    private const string Df = """
        {
          "Images": [
            {"ID":"sha256:aaa","Repository":"ghcr.io/acme/app","Tag":"1.4","Size":"350MB","UniqueSize":"119.7MB","Containers":"1","CreatedSince":"2 days ago"},
            {"ID":"sha256:bbb","Repository":"postgres","Tag":"16","Size":"420MB","UniqueSize":"420MB","Containers":"0","CreatedSince":"3 weeks ago"},
            {"ID":"sha256:ccc","Repository":"<none>","Tag":"<none>","Size":"1.2GB","UniqueSize":"1.2GB","Containers":"0","CreatedSince":"5 days ago"}
          ],
          "Containers": [
            {"ID":"c1","Names":"aspireui-abc12345-app-1","Image":"ghcr.io/acme/app:1.4","State":"exited","Size":"12MB","Labels":"com.docker.compose.project=aspireui-abc12345","CreatedAt":"2026-09-01"},
            {"ID":"c2","Names":"something-else","Image":"nginx","State":"running","Size":"4MB","Labels":"","CreatedAt":"2026-09-02"},
            {"ID":"c3","Names":"old-test","Image":"busybox","State":"exited","Size":"800kB","Labels":"","CreatedAt":"2026-08-01"},
            {"ID":"c4","Names":"aspireui","Image":"ghcr.io/fgilde/aspireui","State":"exited","Size":"90MB","Labels":"","CreatedAt":"2026-08-01"}
          ],
          "Volumes": [
            {"Name":"aspireui-abc12345_data","Links":"0","Size":"2.5GB","Driver":"local","Labels":"com.docker.compose.project=aspireui-abc12345"},
            {"Name":"aspireui-data","Links":"0","Size":"40MB","Driver":"local","Labels":""},
            {"Name":"some-project_photos","Links":"1","Size":"600GB","Driver":"local","Labels":""},
            {"Name":"planned-but-never-started","Links":"0","Size":"0B","Driver":"local","Labels":""},
            {"Name":"7afb8d052d1e","Links":"0","Size":"120MB","Driver":"local","Labels":"com.docker.volume.anonymous="}
          ],
          "BuildCache": [
            {"ID":"b1","Size":"10GB","InUse":"false"},
            {"ID":"b2","Size":"2GB","InUse":"true"}
          ]
        }
        """;

    private static StorageReport Report() =>
        StorageService.Classify(Df, ["aspireui-abc12345"], ["planned-but-never-started"], c => c.Name == "aspireui");

    private static StorageGroup Group(string kind) => Report().Groups.Single(g => g.Kind == kind);
    private static IEnumerable<string> Names(string kind) => Group(kind).Items.Select(i => i.Name);

    [Fact]
    public void A_stopped_hosted_app_keeps_its_container_its_image_and_its_data()
    {
        var report = Report();
        Assert.DoesNotContain("aspireui-abc12345-app-1", Names(StorageService.Containers));
        Assert.DoesNotContain("aspireui-abc12345_data", Names(StorageService.Volumes));
        Assert.DoesNotContain("ghcr.io/acme/app:1.4", Names(StorageService.Images));

        var kept = report.InUse.Single(i => i.Name == "aspireui-abc12345_data");
        Assert.Contains("aspireui-abc12345", kept.Reason);
    }

    [Fact]
    public void AspireUI_never_offers_itself_or_its_own_data()
    {
        Assert.DoesNotContain("aspireui", Names(StorageService.Containers));
        Assert.DoesNotContain("aspireui-data", Names(StorageService.Volumes));
    }

    [Fact]
    public void A_running_container_and_the_volume_attached_to_it_stay()
    {
        Assert.DoesNotContain("something-else", Names(StorageService.Containers));
        Assert.DoesNotContain("some-project_photos", Names(StorageService.Volumes));
    }

    [Fact]
    public void A_volume_a_saved_stack_asks_for_is_not_an_abandoned_one()
    {
        // Nothing is attached to it and it carries no label — it looks exactly like a leftover, and
        // the only thing that tells them apart is the stack that is waiting to use it.
        Assert.DoesNotContain("planned-but-never-started", Names(StorageService.Volumes));
    }

    [Fact]
    public void What_is_left_over_is_offered_with_a_reason()
    {
        Assert.Equal(["postgres:16", "<untagged>"], Names(StorageService.Images).Order().Reverse());
        Assert.Equal(["old-test"], Names(StorageService.Containers));
        Assert.Equal(["7afb8d052d1e"], Names(StorageService.Volumes));

        Assert.All(Report().Groups.SelectMany(g => g.Items), i => Assert.NotEmpty(i.Reason));
        Assert.Contains("anonymous", Group(StorageService.Volumes).Items.Single().Reason);
    }

    [Fact]
    public void The_sizes_add_up_to_what_would_actually_come_back()
    {
        // Images count what is theirs alone: 420MB + 1.2GB, not the size they report with shared layers.
        Assert.Equal(1_620_000_000, Group(StorageService.Images).Bytes);
        Assert.Equal(800_000, Group(StorageService.Containers).Bytes);
        Assert.Equal(120_000_000, Group(StorageService.Volumes).Bytes);
        // Only the part of the build cache nothing is using.
        Assert.Equal(10_000_000_000, Group(StorageService.BuildCache).Bytes);

        var report = Report();
        Assert.Equal(report.Groups.Sum(g => g.Bytes), report.ReclaimableBytes);
        Assert.True(report.InUseBytes > report.ReclaimableBytes, "the 600GB volume in use dwarfs the rest");
    }

    [Fact]
    public void Auto_clean_leaves_data_alone_unless_it_is_told_otherwise()
    {
        Assert.DoesNotContain(StorageService.Volumes, StorageService.SafeKinds);
        Assert.Contains(StorageService.Volumes, StorageService.AllKinds);
    }

    [Theory]
    [InlineData("0B", 0L)]
    [InlineData("N/A", 0L)]
    [InlineData("", 0L)]
    [InlineData("350MB", 350_000_000L)]
    [InlineData("1.2GB", 1_200_000_000L)]
    [InlineData("119.7MB", 119_700_000L)]
    [InlineData("800kB", 800_000L)]
    [InlineData("4KiB", 4096L)]
    [InlineData("2GiB", 2147483648L)]
    public void Docker_prints_sizes_for_people_and_they_are_read_back_as_numbers(string size, long expected)
    {
        Assert.Equal(expected, StorageService.Bytes(size));
    }

    [Fact]
    public void One_label_is_picked_out_of_dockers_list()
    {
        const string labels = "com.docker.compose.project=aspireui-abc,com.docker.compose.version=2.40.3,com.docker.volume.anonymous=";
        Assert.Equal("aspireui-abc", StorageService.Label(labels, "com.docker.compose.project"));
        Assert.Equal("", StorageService.Label(labels, "com.docker.volume.anonymous"));
        Assert.Null(StorageService.Label(labels, "com.docker.compose.nothing"));
    }

    [Fact]
    public void Docker_not_answering_is_reported_rather_than_read_as_nothing_to_keep()
    {
        var report = StorageService.Classify("not json at all", [], [], _ => false);
        Assert.NotNull(report.Error);
        Assert.Empty(report.Groups);
    }
}
