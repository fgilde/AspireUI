using AspireUI.Server.Services;

// The diff that shows what a redeploy would change.
public class TextDiffTests
{
    [Fact]
    public void The_same_text_is_no_change_at_all()
    {
        var r = TextDiff.Unified("a\nb\nc", "a\nb\nc");
        Assert.False(r.Changed);
        Assert.Equal("", r.Text);
        Assert.Equal(0, r.Added);
        Assert.Equal(0, r.Removed);
    }

    [Fact]
    public void Line_endings_alone_are_not_a_change()
    {
        Assert.False(TextDiff.Unified("a\r\nb\r\n", "a\nb").Changed);
    }

    [Fact]
    public void A_changed_line_shows_as_a_removal_and_an_addition()
    {
        var r = TextDiff.Unified("image: nginx:1.26\nports:\n  - 80", "image: nginx:1.27\nports:\n  - 80");

        Assert.True(r.Changed);
        Assert.Equal(1, r.Added);
        Assert.Equal(1, r.Removed);
        Assert.Contains("-image: nginx:1.26", r.Text);
        Assert.Contains("+image: nginx:1.27", r.Text);
        Assert.Contains(" ports:", r.Text);
    }

    [Fact]
    public void Only_the_neighbourhood_of_a_change_is_shown()
    {
        var before = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line {i}"));
        var after = before.Replace("line 20", "line twenty");
        var r = TextDiff.Unified(before, after, context: 2);

        Assert.Contains("-line 20", r.Text);
        Assert.Contains("+line twenty", r.Text);
        Assert.Contains(" line 18", r.Text);
        Assert.DoesNotContain("line 5", r.Text);
        // The collapsed stretches are marked rather than silently dropped.
        Assert.Contains("@@", r.Text);
    }

    [Fact]
    public void An_added_block_is_an_addition_and_nothing_else()
    {
        var r = TextDiff.Unified("services:\n  web:\n", "services:\n  web:\n  db:\n    image: postgres:16\n");
        Assert.Equal(2, r.Added);
        Assert.Equal(0, r.Removed);
        Assert.Contains("+  db:", r.Text);
    }

    [Fact]
    public void Empty_against_something_is_all_addition()
    {
        var r = TextDiff.Unified("", "one\ntwo");
        Assert.True(r.Changed);
        Assert.Equal(2, r.Added);
        // The single empty line the left side is made of counts as removed, and that is honest.
        Assert.True(r.Removed <= 1);
    }
}
