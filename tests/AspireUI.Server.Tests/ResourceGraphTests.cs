using Aspire.DashboardService.Proto.V1;
using AspireUI.Server.Services;

namespace AspireUI.Server.Tests;

// Tests resource graph mapping from gRPC resources.
public class ResourceGraphTests
{
    [Fact]
    public void Map_ExtractsParentRelationship_AndUrls()
    {
        var r = new Resource
        {
            Name = "supabase-db-abc123",
            DisplayName = "supabase-db",
            ResourceType = "Container",
            State = "Running",
            StateStyle = "success",
            IsHidden = false,
        };
        r.Relationships.Add(new ResourceRelationship { ResourceName = "supabase", Type = "Parent" });
        r.Relationships.Add(new ResourceRelationship { ResourceName = "other", Type = "WaitFor" });
        r.Urls.Add(new Url { EndpointName = "http", FullUrl = "http://localhost:5432", IsInternal = false, IsInactive = false });

        var live = ResourceGraphService.Map(r);

        Assert.Equal("supabase-db-abc123", live.Name);
        Assert.Equal("supabase-db", live.DisplayName);
        Assert.Equal("Running", live.State);
        Assert.Equal("supabase", live.Parent);
        Assert.False(live.Hidden);
        var url = Assert.Single(live.Urls);
        Assert.Equal("http://localhost:5432", url.Url);
        Assert.Equal("http", url.Name);
    }

    [Fact]
    public void Map_NoParent_WhenNoParentRelationship()
    {
        var r = new Resource { Name = "web", DisplayName = "web", ResourceType = "Container", State = "Starting" };
        var live = ResourceGraphService.Map(r);
        Assert.Null(live.Parent);
        Assert.Empty(live.Urls);
    }
}
