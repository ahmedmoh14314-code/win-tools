using WinTools.Core.Jobs;

namespace WinTools.Core.Tests;

public sealed class SignPlanTests
{
    [Fact]
    public void A_drawn_signature_on_one_page_has_one_spelling_and_reads_back()
    {
        var plan = new SignPlan(2, 0.75, 0.88, 0.3, [new InkStroke([(10, 20), (30, 40)]), new InkStroke([(500, 200)])], null);
        var text = plan.Format();
        Assert.Equal("page=2;x=0.75;y=0.88;w=0.3;ink=10,20 30,40|500,200", text);
        var read = SignPlan.Read(text, 3)!;
        Assert.Equal(2, read.Page);
        Assert.Equal(2, read.Ink!.Count);
        Assert.Equal((500, 200), read.Ink[1].Points[0]);
    }

    [Fact]
    public void A_typed_signature_on_every_page_is_escaped_and_reads_back()
    {
        var text = new SignPlan(null, 0.5, 0.5, 0.25, null, "أحمد; Ali").Format();
        Assert.Equal("page=all;x=0.5;y=0.5;w=0.25;text=%D8%A3%D8%AD%D9%85%D8%AF%3B%20Ali", text);
        Assert.Equal("أحمد; Ali", SignPlan.Read(text, 1)!.Text);
    }

    [Theory]
    [InlineData("page=4;x=0.5;y=0.5;w=0.3;ink=1,1")] // no page 4
    [InlineData("page=1;x=1.5;y=0.5;w=0.3;ink=1,1")] // off the page
    [InlineData("page=1;x=0.5;y=0.5;w=0.01;ink=1,1")] // too small to see
    [InlineData("page=1;x=0.5;y=0.5;w=0.3;ink=1001,1")] // outside the box
    [InlineData("page=1;x=0.5;y=0.5;w=0.3;ink=")]
    [InlineData("page=1;x=0.5;y=0.5;w=0.3;ink=1,1;ink=2,2")]
    [InlineData("page=01;x=0.5;y=0.5;w=0.3;ink=1,1")] // another spelling
    [InlineData("page=1;x=.5;y=0.5;w=0.3;ink=1,1")]
    [InlineData("page=1;x=0.5;y=0.5;w=0.3;text=%E2%80%AEevil")] // a direction override
    [InlineData("page=1;x=0.5;y=0.5;w=0.3;text=%20padded")]
    [InlineData("page=1;x=0.5;y=0.5;w=0.3;foo=1")]
    public void Anything_else_reads_as_nothing(string text) => Assert.Null(SignPlan.Read(text, 3));

    [Fact]
    public void Too_many_points_read_as_nothing()
    {
        var stroke = new InkStroke([.. Enumerable.Range(0, SignPlan.MaxPoints + 1).Select(i => (i % 1000, 1))]);
        Assert.Null(SignPlan.Read(new SignPlan(1, 0.5, 0.5, 0.3, [stroke], null).Format(), 1));
    }
}
